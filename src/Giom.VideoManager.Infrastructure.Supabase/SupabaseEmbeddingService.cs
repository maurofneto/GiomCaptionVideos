using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System.Net.Http.Headers;
using System.Text;

namespace Giom.VideoManager.Infrastructure.Supabase;

public class SupabaseEmbeddingOptions
{
    public string Url { get; init; } = string.Empty;

    public string PublishableKey { get; init; } = string.Empty;

    /// <summary>Chave secreta (sb_secret_...). Usada no Bearer para escrita.</summary>
    public string SecretKey { get; init; } = string.Empty;
}

/// <summary>
/// Implementação legado de IEmbeddingService via Edge Functions. NÃO é o caminho de
/// busca/indexação em produção: Composition registra OpenAI/Voyage/Cohere keyed
/// (AddEmbeddingService) e a busca passa por IVideoChunkRepository.BuscarSimilaresAsync
/// (RPC PostgREST match_chunks_hibrido). AddSupabaseEmbedding não é chamado.
/// IndexarAsync ainda aponta para /functions/v1/indexar-video (sem equivalente no PostgREST).
/// </summary>
public class SupabaseEmbeddingService : IEmbeddingService
{
    private readonly SupabaseEmbeddingOptions _options;
    private readonly ILogger<SupabaseEmbeddingService> _logger;
    private readonly HttpClient _http;

    public SupabaseEmbeddingService(IOptions<SupabaseEmbeddingOptions> options, ILogger<SupabaseEmbeddingService> logger, IHttpClientFactory httpClientFactory)
    {
        _options = options.Value;
        _logger = logger;

        _http = httpClientFactory.CreateClient("supabase");
        // Aulas longas levam tempo — timeout generoso
        _http.Timeout = TimeSpan.FromMinutes(10);
    }

    public async Task IndexarAsync(Giom.VideoManager.Domain.Entities.Video video, CancellationToken ct = default)
    {
        _logger.LogInformation("Solicitando indexação de chunks: {VideoId}", video.Id);

        var payload = new { video_id = video.Id };
        var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

        var response = await _http.PostAsync($"{_options.Url}/functions/v1/indexar-video", content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Falha ao indexar vídeo {VideoId}: {Status} — {Body}", video.Id, response.StatusCode, body);
            throw new HttpRequestException($"Edge Function indexar-video falhou: {response.StatusCode}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        var result = JsonConvert.DeserializeAnonymousType(json, new { chunks_indexados = 0 });
        _logger.LogInformation("Indexação concluída: {VideoId} — {Chunks} chunks", video.Id, result?.chunks_indexados);
    }

    /// <summary>
    /// Não usado: a busca semântica vai por OpenAI/Voyage/Cohere + IVideoChunkRepository.
    /// Mantido só porque a classe ainda implementa IEmbeddingService.
    /// </summary>
    public async Task<BuscaSemanticaResultado> BuscarAsync(
        string pergunta,
        string? area = null,
        string? canalId = null,
        int limite = 6,
        double threshold = 0.35,
        CancellationToken ct = default)
    {
        var payload = new { pergunta, area, limite, threshold, canal_id = canalId };
        var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

        var response = await _http.PostAsync($"{_options.Url}/functions/v1/buscar-similar", content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Falha na busca semântica: {Status} — {Body}", response.StatusCode, body);
            return new BuscaSemanticaResultado([], "Nenhuma aula relevante encontrada.");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        var result = JsonConvert.DeserializeAnonymousType(json, new
        {
            chunks = new[] { new
            {
                video_id    = "",
                titulo      = "",
                chunk_index = 0,
                conteudo    = "",
                area        = (string?)null,
                subcategoria = (string?)null,
                similarity  = 0.0
            }},
            contexto_formatado = ""
        });

        var chunks = result?.chunks?
            .Select(c => new ChunkSimilar(c.video_id, c.titulo, c.chunk_index, c.conteudo, c.area, c.subcategoria, c.similarity))
            .ToList() ?? [];

        return new BuscaSemanticaResultado(chunks, result?.contexto_formatado ?? "");
    }
}
