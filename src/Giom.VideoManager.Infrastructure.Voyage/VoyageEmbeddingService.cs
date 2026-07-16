using System.Net.Http.Headers;
using System.Text;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Resiliencia;
using Giom.VideoManager.Infrastructure.Supabase;
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
    private readonly HttpClient _supabaseFunctions;

    public VoyageEmbeddingService(
        IOptions<VoyageEmbeddingOptions> options,
        IOptions<SupabaseOptions> supabaseOptions,
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

        // Apenas para chamar Edge Functions (buscar-similar) — mesmo padrão de
        // OpenAIEmbeddingService, não acessa tabelas diretamente.
        var sb = supabaseOptions.Value;
        _supabaseFunctions = httpClientFactory.CreateClient("supabase-functions");
        _supabaseFunctions.BaseAddress = new Uri(sb.Url);
        _supabaseFunctions.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", sb.SecretKey);
        _supabaseFunctions.DefaultRequestHeaders.Add("apikey", sb.SecretKey);
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

    // Mesmo padrão de OpenAIEmbeddingService.BuscarAsync — embeda a pergunta aqui mesmo
    // (client-side) e delega o resto (RRF híbrido, diversidade por aula) pra Edge Function
    // buscar-similar, passando embedding+origem (Feature #101) — filtra por Origem="VoyageAI"
    // antes de comparar vetores, nunca mistura com chunks embedados via OpenAI.
    public async Task<BuscaSemanticaResultado> BuscarAsync(
        string pergunta,
        string? area = null,
        string? canalId = null,
        int limite = 6,
        double threshold = 0.35,
        CancellationToken ct = default)
    {
        var (embeddings, _) = await GerarEmbeddingsAsync([pergunta.AsMemory()], ct);
        var embedding = embeddings[0];

        var payload = new { pergunta, embedding, origem = Origem, area, limite, threshold, canal_id = canalId };
        var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

        var response = await _supabaseFunctions.PostAsync("functions/v1/buscar-similar", content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Falha na busca semântica (Voyage): {Status} — {Body}", response.StatusCode, body);
            return new BuscaSemanticaResultado([], "Nenhuma aula relevante encontrada.");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        var result = JsonConvert.DeserializeAnonymousType(json, new
        {
            chunks = new[] { new
            {
                video_id     = "",
                titulo       = "",
                chunk_index  = 0,
                conteudo     = "",
                area         = (string?)null,
                subcategoria = (string?)null,
                similarity   = 0.0
            }},
            contexto_formatado = ""
        });

        var resultChunks = result?.chunks?
            .Select(c => new ChunkSimilar(c.video_id, c.titulo, c.chunk_index, c.conteudo, c.area, c.subcategoria, c.similarity))
            .ToList() ?? [];

        return new BuscaSemanticaResultado(resultChunks, result?.contexto_formatado ?? "");
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
