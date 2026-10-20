using System.Net.Http.Headers;
using System.Text;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Resiliencia;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Giom.VideoManager.Infrastructure.Voyage;

public class VoyageEmbeddingOptions
{
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "voyage-3";
    public int ChunkSize { get; init; } = 2400;
    public int ChunkOverlap { get; init; } = 400;
    public int EmbedBatchSize { get; init; } = 20;
}

/// <summary>
/// Provider de embedding pra cadeia de fallback (Embedding:FallbackChain, Feature #101). Cada
/// chunk é gravado com Origem = "VoyageAI", e a busca semântica (via IVideoChunkRepository,
/// Domain) filtra por origem antes de comparar vetores — nunca mistura com chunks de outro
/// provedor. Este projeto não conhece Supabase diretamente (nunca referência direta entre dois
/// Infrastructure.*, ver CLAUDE.md).
/// </summary>
public class VoyageEmbeddingService : IEmbeddingService
{
    private const string Origem = "VoyageAI";

    // voyage-3 emite vetores de 1024 dim, mas video_chunk.embedding é halfvec(1536) fixo (Bug
    // #121 — pgvector recusa HNSW sobre coluna sem dimensão, então a coluna tem que ter
    // tamanho fixo). Preencher com zeros não distorce a similaridade de cosseno (não muda
    // produto escalar nem magnitude) e as RPCs match_chunks_hibrido* só comparam dentro da
    // mesma origem, então nunca mistura com vetores OpenAI/Cohere de tamanho real diferente.
    private const int DimensaoAlvo = 1536;

    private readonly VoyageEmbeddingOptions _options;
    private readonly IVideoChunkRepository _chunkRepository;
    private readonly IFarolRateLimiter _farol;
    private readonly ILogger<VoyageEmbeddingService> _logger;
    private readonly IContextoTelemetria _contextoTelemetria;
    private readonly IPricingService _pricingService;
    private readonly HttpClient _voyage;

    public VoyageEmbeddingService(
        IOptions<VoyageEmbeddingOptions> options,
        IVideoChunkRepository chunkRepository,
        IFarolRateLimiter farol,
        IHttpClientFactory httpClientFactory,
        ILogger<VoyageEmbeddingService> logger,
        IContextoTelemetria contextoTelemetria,
        IPricingService pricingService)
    {
        _options         = options.Value;
        _chunkRepository = chunkRepository;
        _farol           = farol;
        _logger          = logger;
        _contextoTelemetria = contextoTelemetria;
        _pricingService = pricingService;

        _voyage = httpClientFactory.CreateClient("voyage");
        _voyage.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
    }

    public async Task IndexarAsync(Video video, CancellationToken ct = default)
    {
        if (video.Chunks.Any(c => c.Origem == Origem))
        {
            _logger.LogInformation("{VideoId} já tem chunks da origem {Origem} — indexação ignorada", video.Id, Origem);
            return;
        }

        if (video.Transcricao == null)
        {
            _logger.LogWarning("Transcrição não encontrada para {VideoId} — indexação ignorada", video.Id);
            return;
        }

        var conteudo = video.Transcricao.ConteudoLimpo;
        var chunks = TextChunker.Dividir(conteudo, _options.ChunkSize, _options.ChunkOverlap);

        _logger.LogInformation("{VideoId} — {Chunks} chunks de {Chars} chars (Voyage)", video.Id, chunks.Count, conteudo.Length);

        var (embeddings, tokensTotais) = await GerarEmbeddingsAsync(chunks, ct);

        _contextoTelemetria.Registrar(
            provedor: "VoyageAI",
            modelo: _options.Model,
            tokensEntrada: tokensTotais,
            custoEstimadoUsd: await _pricingService.CalcularAsync("VoyageAI", _options.Model, tokensTotais, ct: ct));

        await _chunkRepository.DeletarPorVideoAsync(video.Id, Origem, ct);
        await _chunkRepository.InserirAsync(
            chunks.Select((mem, i) => new VideoChunk
            {
                VideoId    = video.Id,
                Titulo     = video.Title,
                ChunkIndex = i,
                Conteudo   = mem.ToString(),
                Embedding  = embeddings[i],
                Origem     = Origem
            }), ct);

        _logger.LogInformation("{VideoId} — {Chunks} chunks indexados com sucesso (Voyage)", video.Id, chunks.Count);
    }

    // Mesmo padrão de OpenAIEmbeddingService.BuscarAsync — embeda a pergunta aqui mesmo
    // (client-side) e delega o resto (RRF híbrido, diversidade por aula, filtro por origem) pro
    // IVideoChunkRepository (Feature #101) — nunca mistura com chunks embedados via OpenAI.
    public async Task<BuscaSemanticaResultado> BuscarAsync(
        string pergunta,
        string? area = null,
        string? canalId = null,
        int limite = 6,
        double threshold = 0.35,
        CancellationToken ct = default)
    {
        var (embeddings, tokensTotais) = await GerarEmbeddingsAsync([pergunta.AsMemory()], ct);

        _contextoTelemetria.Registrar(
            provedor: "VoyageAI",
            modelo: _options.Model,
            tokensEntrada: tokensTotais,
            custoEstimadoUsd: await _pricingService.CalcularAsync("VoyageAI", _options.Model, tokensTotais, ct: ct));

        return await _chunkRepository.BuscarSimilaresAsync(pergunta, embeddings[0], Origem, area, canalId, limite, threshold, ct);
    }

    private async Task<(List<float[]> Embeddings, int TokensTotais)> GerarEmbeddingsAsync(List<ReadOnlyMemory<char>> chunks, CancellationToken ct)
    {
        var all = new List<float[]>();
        var tokensTotais = 0;

        for (int i = 0; i < chunks.Count; i += _options.EmbedBatchSize)
        {
            var batch = chunks.Skip(i).Take(_options.EmbedBatchSize).Select(m => m.ToString()).ToList();

            var payload = new { model = _options.Model, input = batch };
            var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            // Bug #122: sem o farol, N tasks concorrentes (autoscaling) disparavam batches ao
            // mesmo tempo contra os limites de RPM/TPM do Voyage, estourando 429 mesmo com o
            // retry do Polly — o farol serializa isso entre TODAS as tasks, não só threads
            // locais. As duas dimensões são pedidas juntas (tudo-ou-nada): o 429 real capturado
            // no Bug #122 citava "3 RPM and 10K TPM" como limites independentes do Voyage.
            var tokensEstimados = batch.Sum(t => t.Length) / 4.0;
            await _farol.AguardarAsync("VoyageAI", new Dictionary<string, double>
            {
                [RateLimiterDimensao.Requisicoes] = 1,
                [RateLimiterDimensao.Tokens] = tokensEstimados
            }, ct);
            var response = await _voyage.PostAsync("v1/embeddings", content, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Voyage embeddings falhou: {response.StatusCode} — {json}", inner: null, statusCode: response.StatusCode);

            var result = JsonConvert.DeserializeAnonymousType(json, new
            {
                data = new[] { new { index = 0, embedding = new float[0] } },
                usage = new { total_tokens = 0 }
            });

            all.AddRange(result!.data.OrderBy(d => d.index).Select(d => Preencher(d.embedding)));
            tokensTotais += result.usage?.total_tokens ?? 0;

            if (i + _options.EmbedBatchSize < chunks.Count)
                await Task.Delay(200, ct);
        }

        return (all, tokensTotais);
    }

    private static float[] Preencher(float[] embedding)
    {
        if (embedding.Length >= DimensaoAlvo) return embedding;
        var preenchido = new float[DimensaoAlvo];
        Array.Copy(embedding, preenchido, embedding.Length);
        return preenchido;
    }
}
