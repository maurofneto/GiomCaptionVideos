using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System.Net.Http.Headers;
using System.Text;

namespace Giom.VideoManager.Infrastructure.Supabase;

public class SupabaseVideoChunkRepository : IVideoChunkRepository
{
    private readonly HttpClient _http;
    private readonly ILogger<SupabaseVideoChunkRepository> _logger;

    private const int BatchSize = 20;

    public SupabaseVideoChunkRepository(IOptions<SupabaseOptions> options, IHttpClientFactory httpClientFactory, ILogger<SupabaseVideoChunkRepository> logger)
    {
        _logger = logger;

        var sb = options.Value;
        _http = httpClientFactory.CreateClient("supabase");
        _http.BaseAddress = new Uri(sb.Url);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sb.SecretKey);
        _http.DefaultRequestHeaders.Add("apikey", sb.SecretKey);
        _http.Timeout = TimeSpan.FromMinutes(5);
    }

    public async Task DeletarPorVideoAsync(string videoId, string origem, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/rest/v1/video_chunk?video_id=eq.{videoId}&origem=eq.{origem}");
        request.Headers.Add("Prefer", "return=minimal");

        var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            _logger.LogWarning("Falha ao deletar chunks de {VideoId} (origem {Origem}): {Status}", videoId, origem, response.StatusCode);
    }

    public async Task InserirAsync(IEnumerable<VideoChunk> chunks, CancellationToken ct = default)
    {
        var list = chunks.ToList();

        for (int i = 0; i < list.Count; i += BatchSize)
        {
            var batch = list.Skip(i).Take(BatchSize).Select(c => new
            {
                video_id    = c.VideoId,
                titulo      = c.Titulo,
                chunk_index = c.ChunkIndex,
                conteudo    = c.Conteudo,
                embedding   = c.Embedding,
                origem      = c.Origem
            });

            var content = new StringContent(JsonConvert.SerializeObject(batch), Encoding.UTF8, "application/json");
            var request = new HttpRequestMessage(HttpMethod.Post, "/rest/v1/video_chunk") { Content = content };
            request.Headers.Add("Prefer", "resolution=merge-duplicates,return=minimal");

            var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"Falha ao inserir chunks [{i}..{i + BatchSize - 1}]: {response.StatusCode} — {body}");
            }

            _logger.LogDebug("Chunks inseridos: {Done}/{Total}", Math.Min(i + BatchSize, list.Count), list.Count);
        }
    }

    public async Task<IReadOnlyList<string>> ListarVideoIdsPorOrigemAsync(string origem, CancellationToken ct = default)
    {
        // O PostgREST corta em 1000 linhas por página por padrão — video_chunk tem muito mais
        // linhas que isso por origem (cada vídeo vira dezenas de chunks), então uma chamada sem
        // paginação devolve só uma fatia arbitrária dos vídeos, não o corpus inteiro. Pagina via
        // Range até a página vir mais curta que o tamanho pedido (fim dos dados).
        const int paginaTamanho = 1000;
        var videoIds = new HashSet<string>();

        for (var offset = 0; ; offset += paginaTamanho)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/rest/v1/video_chunk?select=video_id&origem=eq.{origem}");
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + paginaTamanho - 1);

            var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var linhas = JsonConvert.DeserializeObject<List<Dictionary<string, string>>>(json) ?? [];

            foreach (var linha in linhas)
                videoIds.Add(linha["video_id"]);

            if (linhas.Count < paginaTamanho) break;
        }

        return videoIds.Order().ToList();
    }

    public async Task<BuscaSemanticaResultado> BuscarSimilaresAsync(
        string pergunta,
        float[] embedding,
        string origem,
        string? area = null,
        string? canalId = null,
        int limite = 6,
        double threshold = 0.35,
        CancellationToken ct = default)
    {
        var payload = new { pergunta, embedding, origem, area, limite, threshold, canal_id = canalId };
        var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

        var response = await _http.PostAsync("/functions/v1/buscar-similar", content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Falha na busca semântica ({Origem}): {Status} — {Body}", origem, response.StatusCode, body);
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

        var chunks = result?.chunks?
            .Select(c => new ChunkSimilar(c.video_id, c.titulo, c.chunk_index, c.conteudo, c.area, c.subcategoria, c.similarity))
            .ToList() ?? [];

        return new BuscaSemanticaResultado(chunks, result?.contexto_formatado ?? "");
    }
}
