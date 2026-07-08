using System.Net.Http.Headers;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Supabase;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Giom.VideoManager.ObservabilidadeTeste;

/// <summary>
/// Mesmo padrão de SupabaseCaptionService: em vez de chamar Claude, reaproveita o resumo REAL já
/// gerado (em execução anterior de verdade) pro vídeo de origem — video.PlaylistId carrega o Id
/// real, ver SupabaseVideoSource. Zero chamada de IA; ainda assim exercita span/telemetria/
/// persistência do resumo ponta a ponta.
/// </summary>
public class SupabaseResumoService : IResumoService
{
    private readonly HttpClient _http;

    public SupabaseResumoService(IOptions<SupabaseOptions> options, IHttpClientFactory httpClientFactory)
    {
        var opcoes = options.Value;
        _http = httpClientFactory.CreateClient("supabase");
        _http.BaseAddress = new Uri(opcoes.Url);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", opcoes.SecretKey);
        _http.DefaultRequestHeaders.Add("apikey", opcoes.SecretKey);
    }

    public async Task<Resumo?> GerarResumoAsync(Video video, CancellationToken ct = default)
    {
        var idOrigem = video.PlaylistId;
        if (string.IsNullOrEmpty(idOrigem)) return null;

        var response = await _http.GetAsync($"/rest/v1/resumo?video_id=eq.{idOrigem}&select=descricao,origem", ct);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync(ct);
        var linha = JsonConvert.DeserializeObject<List<ResumoRowDto>>(json)?.FirstOrDefault();
        if (linha == null) return null;

        return new Resumo
        {
            VideoId   = video.Id,
            Descricao = linha.descricao,
            Origem    = $"Supabase (emprestado de {idOrigem})",
            GeradoEm  = DateTime.UtcNow,
            Capitulos = []
        };
    }

    private record ResumoRowDto(string descricao, string? origem);
}
