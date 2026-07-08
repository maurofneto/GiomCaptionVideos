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
/// Provider de embedding selecionável via Embedding:Provider = "VoyageAI" (Feature #101) — NÃO
/// entra na cadeia de fallback automática. Vetores de provedores diferentes (OpenAI vs Voyage)
/// não são comparáveis entre si por similaridade de cosseno, mesmo com a mesma dimensão —
/// misturar os dois no mesmo índice pgvector corromperia a busca semântica silenciosamente. Por
/// isso BuscarAsync não é suportado aqui: a Edge Function buscar-similar embeda a pergunta via
/// OpenAI do lado do servidor, então trocar só a indexação não resolveria a mistura de espaços
/// na hora da busca — precisaria reescrever a Edge Function, fora de escopo desta entrega.
/// </summary>
public class VoyageEmbeddingService : IEmbeddingService
{
    private readonly VoyageEmbeddingOptions _options;
    private readonly IVideoChunkRepository _chunkRepository;
    private readonly ILogger<VoyageEmbeddingService> _logger;
    private readonly IContextoTelemetria _contextoTelemetria;
    private readonly HttpClient _voyage;

    public VoyageEmbeddingService(
        IOptions<VoyageEmbeddingOptions> options,
        IVideoChunkRepository chunkRepository,
        IHttpClientFactory httpClientFactory,
        ILogger<VoyageEmbeddingService> logger,
        IContextoTelemetria contextoTelemetria)
    {
        _options         = options.Value;
        _chunkRepository = chunkRepository;
        _logger          = logger;
        _contextoTelemetria = contextoTelemetria;

        _voyage = httpClientFactory.CreateClient("voyage");
        _voyage.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
    }

    public async Task IndexarAsync(Video video, CancellationToken ct = default)
    {
        if (video.Chunks.Count > 0)
        {
            _logger.LogInformation("{VideoId} já tem {Count} chunks — indexação ignorada", video.Id, video.Chunks.Count);
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
            tokensEntrada: tokensTotais);

        await _chunkRepository.DeletarPorVideoAsync(video.Id, ct);
        await _chunkRepository.InserirAsync(
            chunks.Select((mem, i) => new VideoChunk
            {
                VideoId    = video.Id,
                Titulo     = video.Title,
                ChunkIndex = i,
                Conteudo   = mem.ToString(),
                Embedding  = embeddings[i]
            }), ct);

        _logger.LogInformation("{VideoId} — {Chunks} chunks indexados com sucesso (Voyage)", video.Id, chunks.Count);
    }

    public Task<BuscaSemanticaResultado> BuscarAsync(string pergunta, string? area = null, string? canalId = null, int limite = 6, double threshold = 0.35, CancellationToken ct = default)
        => throw new NotSupportedException(
            "VoyageEmbeddingService só indexa — a busca semântica (buscar-similar) embeda a pergunta via OpenAI do lado do servidor, então misturar com embeddings Voyage corromperia a comparação de vetores. Reescrever a Edge Function pra suportar múltiplos embedders é um esforço separado (ver Feature #101).");

    private async Task<(List<float[]> Embeddings, int TokensTotais)> GerarEmbeddingsAsync(List<ReadOnlyMemory<char>> chunks, CancellationToken ct)
    {
        var all = new List<float[]>();
        var tokensTotais = 0;

        for (int i = 0; i < chunks.Count; i += _options.EmbedBatchSize)
        {
            var batch = chunks.Skip(i).Take(_options.EmbedBatchSize).Select(m => m.ToString()).ToList();

            var payload = new { model = _options.Model, input = batch };
            var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            var response = await _voyage.PostAsync("v1/embeddings", content, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Voyage embeddings falhou: {response.StatusCode} — {json}", inner: null, statusCode: response.StatusCode);

            var result = JsonConvert.DeserializeAnonymousType(json, new
            {
                data = new[] { new { index = 0, embedding = new float[0] } },
                usage = new { total_tokens = 0 }
            });

            all.AddRange(result!.data.OrderBy(d => d.index).Select(d => d.embedding));
            tokensTotais += result.usage?.total_tokens ?? 0;

            if (i + _options.EmbedBatchSize < chunks.Count)
                await Task.Delay(200, ct);
        }

        return (all, tokensTotais);
    }
}
