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
/// Provider de embedding pra cadeia de fallback (Embedding:FallbackChain, Feature #101) — fora
/// da cadeia padrão por enquanto ("mantenha somente OpenAI"). Cada chunk é gravado com
/// Origem = "VoyageAI", e o Supabase filtra por origem na busca — vetores Voyage nunca são
/// comparados com vetores OpenAI (espaços vetoriais incompatíveis).
/// </summary>
public class VoyageEmbeddingService : IEmbeddingService
{
    private const string Origem = "VoyageAI";

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
                Embedding  = embeddings[i],
                Origem     = Origem
            }), ct);

        _logger.LogInformation("{VideoId} — {Chunks} chunks indexados com sucesso (Voyage)", video.Id, chunks.Count);
    }

    // Desde a Feature #101 a Edge Function filtra por origem (não embeda mais server-side) —
    // misturar embeddings Voyage/OpenAI deixou de ser um problema arquitetural. BuscarAsync só
    // não está implementado ainda porque Voyage fica fora da cadeia de fallback por enquanto
    // ("mantenha somente OpenAI") — sem uso real hoje. Seguiria o mesmo padrão de
    // OpenAIEmbeddingService.BuscarAsync quando for necessário: gerar o embedding da pergunta
    // aqui, enviar {pergunta, embedding, origem: "VoyageAI", ...} pra buscar-similar.
    public Task<BuscaSemanticaResultado> BuscarAsync(string pergunta, string? area = null, string? canalId = null, int limite = 6, double threshold = 0.35, CancellationToken ct = default)
        => throw new NotSupportedException("VoyageEmbeddingService.BuscarAsync ainda não implementado — Voyage está fora da cadeia de fallback nesta entrega (ver Feature #101).");

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
