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
    private readonly SupabaseOptions _options;
    private readonly ILogger<SupabaseVideoChunkRepository> _logger;

    private const int BatchSize = 20;
    private const int MaxChunksPorAula = 2;

    public SupabaseVideoChunkRepository(IOptions<SupabaseOptions> options, IHttpClientFactory httpClientFactory, ILogger<SupabaseVideoChunkRepository> logger)
    {
        this._options = options.Value;
        _logger = logger;

        _http = httpClientFactory.CreateClient("supabase");
        _http.Timeout = TimeSpan.FromMinutes(5);
    }

    public async Task DeletarPorVideoAsync(string videoId, string origem, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, _options.Rest($"/video_chunk?video_id=eq.{videoId}&origem=eq.{origem}"));
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
            var request = new HttpRequestMessage(HttpMethod.Post, _options.Rest("/video_chunk")) { Content = content };
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
        var response = await _http.GetAsync(_options.Rest($"/video_chunk?select=video_id&origem=eq.{origem}"), ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var linhas = JsonConvert.DeserializeObject<List<Dictionary<string, string>>>(json) ?? [];

        return linhas.Select(l => l["video_id"]).Distinct().Order().ToList();
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
        var candidatosLimite = Math.Min(limite * 4, 24);
        var porArea = !string.IsNullOrWhiteSpace(area);
        var rpcPath = porArea ? "/rpc/match_chunks_hibrido_por_area" : "/rpc/match_chunks_hibrido";

        var rpcParams = new Dictionary<string, object?>
        {
            ["query_embedding"] = embedding,
            ["query_texto"] = pergunta,
            ["origem_filtro"] = origem,
            ["match_count"] = candidatosLimite
        };
        if (porArea)
            rpcParams["area_filtro"] = area;
        if (!string.IsNullOrWhiteSpace(canalId))
            rpcParams["canal_id_filtro"] = canalId;

        var content = new StringContent(JsonConvert.SerializeObject(rpcParams), Encoding.UTF8, "application/json");
        var response = await _http.PostAsync(_options.Rest(rpcPath), content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Falha na busca semântica ({Origem}): {Status} — {Body}", origem, response.StatusCode, body);
            return new BuscaSemanticaResultado([], "Nenhuma aula relevante encontrada.");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        var candidatos = (JsonConvert.DeserializeObject<List<ChunkMatchDto>>(json) ?? [])
            .Where(c => c.similarity >= threshold)
            .ToList();

        var porVideo = new Dictionary<string, int>();
        foreach (var c in candidatos)
            porVideo[c.video_id] = porVideo.GetValueOrDefault(c.video_id) + 1;

        var itens = new List<ContextoItem>();
        var resumosUsados = new HashSet<string>();

        foreach (var c in candidatos)
        {
            if (itens.Count >= limite) break;

            if (porVideo[c.video_id] > MaxChunksPorAula)
            {
                if (resumosUsados.Contains(c.video_id)) continue;

                resumosUsados.Add(c.video_id);
                var resumo = await BuscarResumoAsync(c.video_id, ct);

                if (resumo is not null)
                {
                    itens.Add(new ContextoItem("resumo", c.video_id, c.titulo, -1, resumo, c.area, null, c.similarity));
                    continue;
                }
            }

            itens.Add(new ContextoItem("chunk", c.video_id, c.titulo, c.chunk_index, c.conteudo, c.area, c.subcategoria, c.similarity));
        }

        var chunks = itens
            .Select(it => new ChunkSimilar(it.VideoId, it.Titulo, it.ChunkIndex, it.Conteudo, it.Area, it.Subcategoria, it.Similarity))
            .ToList();

        return new BuscaSemanticaResultado(chunks, FormatarContexto(itens));
    }

    private async Task<string?> BuscarResumoAsync(string videoId, CancellationToken ct)
    {
        var response = await _http.GetAsync(_options.Rest($"/resumo?select=descricao&video_id=eq.{videoId}&limit=1"), ct);
        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync(ct);
        var rows = JsonConvert.DeserializeObject<List<ResumoDescricaoDto>>(json);
        var descricao = rows?.FirstOrDefault()?.descricao;
        return string.IsNullOrEmpty(descricao) ? null : descricao;
    }

    private static string FormatarContexto(IReadOnlyList<ContextoItem> itens)
    {
        if (itens.Count == 0)
            return "Nenhuma aula relevante encontrada para esta pergunta.";

        return string.Join("\n\n---\n\n", itens.Select(it =>
        {
            var areaTag = string.IsNullOrEmpty(it.Area) ? "" : $" — {it.Area}";

            if (it.Tipo == "resumo")
                return $"[RESUMO DA AULA: {it.Titulo}{areaTag}]\n\"{it.Conteudo.Trim()}\"";

            var sim = (int)Math.Round(it.Similarity * 100, MidpointRounding.AwayFromZero);
            return $"[AULA: {it.Titulo}{areaTag}]\n(Trecho {it.ChunkIndex + 1} | Relevância: {sim}%)\n\"{it.Conteudo.Trim()}\"";
        }));
    }

    private sealed record ContextoItem(
        string Tipo,
        string VideoId,
        string Titulo,
        int ChunkIndex,
        string Conteudo,
        string? Area,
        string? Subcategoria,
        double Similarity);

    private sealed class ChunkMatchDto
    {
        public string video_id { get; set; } = "";
        public string titulo { get; set; } = "";
        public int chunk_index { get; set; }
        public string conteudo { get; set; } = "";
        public string? area { get; set; }
        public string? subcategoria { get; set; }
        public double similarity { get; set; }
    }

    private sealed class ResumoDescricaoDto
    {
        public string? descricao { get; set; }
    }
}
