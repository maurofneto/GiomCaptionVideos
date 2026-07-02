using Domain.Entities;
using Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Infrastructure.Summary;

/// <summary>
/// Gera resumo usando o serviço público summarize.tech (legado).
/// Mantido para compatibilidade — prefira ClaudeResumoService para novos projetos.
/// </summary>
public class SummarizeTechResumoService : IResumoService
{
    private readonly ILogger<SummarizeTechResumoService> _logger;
    private Guid _deviceId = Guid.NewGuid();

    public SummarizeTechResumoService(ILogger<SummarizeTechResumoService> logger)
    {
        _logger = logger;
    }

    public async Task<Resumo?> GerarResumoAsync(Video video, CancellationToken ct = default)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("Origin", "https://www.summarize.tech");
        http.DefaultRequestHeaders.Referrer = new Uri($"https://www.summarize.tech/www.youtube.com/watch?v={video.Id}");

        var payload = new
        {
            url = $"https://youtube.com/watch?v={video.Id}",
            deviceId = _deviceId.ToString(),
            idToken = ""
        };

        var content = new StringContent(JsonConvert.SerializeObject(payload), System.Text.Encoding.UTF8, "application/json");
        var response = await http.PostAsync("https://www.summarize.tech/api/summary", content, ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("summarize.tech retornou {Status} para vídeo {VideoId}", response.StatusCode, video.Id);
            _deviceId = Guid.NewGuid();
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        var summary = JsonConvert.DeserializeObject<SummaryRoot>(json);

        var rollup = summary?.rollups?
            .Select(x =>
            {
                // Normaliza content se vier vazio
                var content = string.IsNullOrEmpty(x.Value.summary.content)
                    ? x.Value.summary.before + x.Value.summary.keyword + x.Value.summary.after
                    : x.Value.summary.content;
                var normalized = x.Value with { summary = x.Value.summary with { content = content } };
                return new KeyValuePair<string, Rollup>(x.Key, normalized);
            })
            .Aggregate((lr, rr) =>
            {
                foreach (var rc in rr.Value.children) lr.Value.children.TryAdd(rc.Key, rc.Value);
                var merged = lr.Value with { summary = lr.Value.summary with { content = lr.Value.summary.content + rr.Value.summary.content } };
                return new KeyValuePair<string, Rollup>(lr.Key, merged);
            }).Value;

        if (rollup == null) return null;

        return new Resumo
        {
            VideoId = video.Id,
            Descricao = rollup.summary.content,
            Origem = "SummarizeTech",
            GeradoEm = DateTime.UtcNow,
            Capitulos = rollup.children.Select(child => new Capitulo
            {
                Inicio = TimeSpan.FromSeconds(double.Parse(child.Key)),
                Texto = child.Value
            }).ToArray()
        };
    }

    // DTOs internos do summarize.tech
    private record SummaryRoot(Dictionary<string, Rollup>? rollups);
    private record Rollup(Dictionary<string, string> children, SummaryContent summary);
    private record SummaryContent(string content, string before, string keyword, string after);
}
