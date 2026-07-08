using System.Net.Http.Headers;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Enums;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Supabase;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Giom.VideoManager.ObservabilidadeTeste;

/// <summary>
/// Mesmo padrão de SupabaseCaptionService/SupabaseResumoService: reaproveita as categorias REAIS
/// já classificadas (por Claude, em execução anterior) pro vídeo de origem, em vez de chamar IA de
/// novo. video.PlaylistId carrega o Id real de origem — ver SupabaseVideoSource.
/// </summary>
public class SupabaseCategorizacaoService : ICategorizacaoService
{
    private readonly HttpClient _http;

    public SupabaseCategorizacaoService(IOptions<SupabaseOptions> options, IHttpClientFactory httpClientFactory)
    {
        var opcoes = options.Value;
        _http = httpClientFactory.CreateClient("supabase");
        _http.BaseAddress = new Uri(opcoes.Url);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", opcoes.SecretKey);
        _http.DefaultRequestHeaders.Add("apikey", opcoes.SecretKey);
    }

    public async Task<IReadOnlyList<CategoriaTeologica>> ClassificarAsync(Video video, CancellationToken ct = default)
    {
        var idOrigem = video.PlaylistId;
        if (string.IsNullOrEmpty(idOrigem)) return [];

        var response = await _http.GetAsync($"/rest/v1/categoria_teologica?video_id=eq.{idOrigem}&select=area,subcategoria,peso", ct);
        if (!response.IsSuccessStatusCode) return [];

        var json = await response.Content.ReadAsStringAsync(ct);
        var linhas = JsonConvert.DeserializeObject<List<CategoriaRowDto>>(json) ?? [];

        var categorias = new List<CategoriaTeologica>();
        foreach (var linha in linhas)
        {
            if (!Enum.TryParse<AreaTeologica>(linha.area, ignoreCase: true, out var area)) continue;

            categorias.Add(new CategoriaTeologica
            {
                VideoId        = video.Id,
                Area           = area,
                Subcategoria   = linha.subcategoria,
                Peso           = linha.peso,
                ClassificadoEm = DateTime.UtcNow
            });
        }

        return categorias;
    }

    private record CategoriaRowDto(string area, string subcategoria, decimal peso);
}
