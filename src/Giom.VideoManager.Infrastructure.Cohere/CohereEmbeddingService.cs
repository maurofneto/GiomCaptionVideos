using System.Net.Http.Headers;
using System.Text;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Resiliencia;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Giom.VideoManager.Infrastructure.Cohere;

public class CohereEmbeddingOptions
{
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "embed-v4.0";
    public int ChunkSize { get; init; } = 2400;
    public int ChunkOverlap { get; init; } = 400;

    // API v2 do Cohere aceita no máximo 96 textos por chamada — fica com folga do limite.
    public int EmbedBatchSize { get; init; } = 90;
}

/// <summary>
/// Provider de embedding pra cadeia de fallback (Embedding:FallbackChain, Feature #101) — mesmo
/// padrão de OpenAIEmbeddingService/VoyageEmbeddingService. Cada chunk é gravado com
/// Origem = "Cohere", e a busca semântica (via IVideoChunkRepository, Domain) filtra por origem
/// antes de comparar vetores. Este projeto não conhece Supabase diretamente (nunca referência
/// direta entre dois Infrastructure.*, ver CLAUDE.md).
///
/// Particularidade do Cohere: a API v2 exige "input_type" diferente pra indexação
/// (search_document) e pra busca (search_query) — os outros provedores não têm esse conceito.
/// </summary>
public class CohereEmbeddingService : IEmbeddingService
{
    private const string Origem = "Cohere";

    private readonly CohereEmbeddingOptions _options;
    private readonly IVideoChunkRepository _chunkRepository;
    private readonly ILogger<CohereEmbeddingService> _logger;
    private readonly IContextoTelemetria _contextoTelemetria;
    private readonly HttpClient _cohere;

    public CohereEmbeddingService(
        IOptions<CohereEmbeddingOptions> options,
        IVideoChunkRepository chunkRepository,
        IHttpClientFactory httpClientFactory,
        ILogger<CohereEmbeddingService> logger,
        IContextoTelemetria contextoTelemetria)
    {
        _options         = options.Value;
        _chunkRepository = chunkRepository;
        _logger          = logger;
        _contextoTelemetria = contextoTelemetria;

        _cohere = httpClientFactory.CreateClient("cohere");
        _cohere.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
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

        _logger.LogInformation("{VideoId} — {Chunks} chunks de {Chars} chars (Cohere)", video.Id, chunks.Count, conteudo.Length);

        var (embeddings, tokensTotais) = await GerarEmbeddingsAsync(chunks, "search_document", ct);

        _contextoTelemetria.Registrar(
            provedor: "Cohere",
            modelo: _options.Model,
            tokensEntrada: tokensTotais);

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

        _logger.LogInformation("{VideoId} — {Chunks} chunks indexados com sucesso (Cohere)", video.Id, chunks.Count);
    }

    // Mesmo padrão de OpenAIEmbeddingService.BuscarAsync — embeda a pergunta aqui mesmo
    // (client-side, com input_type="search_query") e delega o resto (RRF híbrido, diversidade
    // por aula, filtro por origem) pro IVideoChunkRepository (Feature #101).
    public async Task<BuscaSemanticaResultado> BuscarAsync(
        string pergunta,
        string? area = null,
        string? canalId = null,
        int limite = 6,
        double threshold = 0.35,
        CancellationToken ct = default)
    {
        var (embeddings, tokensTotais) = await GerarEmbeddingsAsync([pergunta.AsMemory()], "search_query", ct);

        _contextoTelemetria.Registrar(
            provedor: "Cohere",
            modelo: _options.Model,
            tokensEntrada: tokensTotais);

        return await _chunkRepository.BuscarSimilaresAsync(pergunta, embeddings[0], Origem, area, canalId, limite, threshold, ct);
    }

    private async Task<(List<float[]> Embeddings, int TokensTotais)> GerarEmbeddingsAsync(
        List<ReadOnlyMemory<char>> chunks, string inputType, CancellationToken ct)
    {
        var all = new List<float[]>();
        var tokensTotais = 0;

        for (int i = 0; i < chunks.Count; i += _options.EmbedBatchSize)
        {
            var batch = chunks.Skip(i).Take(_options.EmbedBatchSize).Select(m => m.ToString()).ToList();

            var payload = new
            {
                model = _options.Model,
                texts = batch,
                input_type = inputType,
                embedding_types = new[] { "float" }
            };
            var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            var response = await _cohere.PostAsync("v2/embed", content, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Cohere embeddings falhou: {response.StatusCode} — {json}", inner: null, statusCode: response.StatusCode);

            var result = JsonConvert.DeserializeAnonymousType(json, new
            {
                embeddings = new { @float = new[] { new float[0] } },
                meta = new { billed_units = new { input_tokens = 0.0 } }
            });

            all.AddRange(result!.embeddings.@float);
            tokensTotais += (int)(result.meta?.billed_units?.input_tokens ?? 0);

            if (i + _options.EmbedBatchSize < chunks.Count)
                await Task.Delay(200, ct);
        }

        return (all, tokensTotais);
    }
}
