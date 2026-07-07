using System.Net.Http.Headers;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure;
using Giom.VideoManager.Infrastructure.Supabase;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Giom.VideoManager.ObservabilidadeTeste;

/// <summary>
/// Busca a legenda real de onde o vídeo de teste "pegou emprestado" o conteúdo — o Id real fica
/// em Video.PlaylistId (ver SupabaseVideoSource), já que o Video.Id em si é sempre novo pra evitar
/// que InstitutoVideoHandler pule o reprocessamento. ConteudoLimpo é recalculado aqui (mesma lógica
/// de SupabaseVideoRepository) — a tabela transcricao só guarda o conteúdo bruto com timestamps.
/// </summary>
public class SupabaseCaptionService : ICaptionService
{
    private readonly HttpClient _http;

    public SupabaseCaptionService(IOptions<SupabaseOptions> options, IHttpClientFactory httpClientFactory)
    {
        var opcoes = options.Value;
        _http = httpClientFactory.CreateClient("supabase");
        _http.BaseAddress = new Uri(opcoes.Url);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", opcoes.SecretKey);
        _http.DefaultRequestHeaders.Add("apikey", opcoes.SecretKey);
    }

    public async Task<Transcricao?> GetCaptionAsync(Video video, CancellationToken ct = default)
    {
        var idOrigem = video.PlaylistId;
        if (string.IsNullOrEmpty(idOrigem)) return null;

        var response = await _http.GetAsync($"/rest/v1/transcricao?video_id=eq.{idOrigem}&select=conteudo,formato,origem", ct);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync(ct);
        var linha = JsonConvert.DeserializeObject<List<TranscricaoRowDto>>(json)?.FirstOrDefault();
        if (linha == null) return null;

        var formato = linha.formato ?? "txt";
        return new Transcricao
        {
            VideoId       = video.Id,
            Conteudo      = linha.conteudo,
            ConteudoLimpo = TranscricaoHelper.StripTimestamps(linha.conteudo, formato),
            Formato       = formato,
            Origem        = $"Supabase (emprestado de {idOrigem})",
            GeradoEm      = DateTime.UtcNow
        };
    }

    private record TranscricaoRowDto(string conteudo, string? formato, string? origem);
}
