using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http.Headers;

namespace Giom.VideoManager.Infrastructure.Supabase;

/// <summary>Lê a tabela canal_ingestao (US #105) via PostgREST — mesmo padrão HTTP de SupabaseProgressoRepository/SupabaseSecretsProvider.</summary>
public class SupabaseCanalIngestaoRepository : ICanalIngestaoRepository
{
    private readonly HttpClient _http;
    private readonly SupabaseOptions _options;

    private readonly ILogger<SupabaseCanalIngestaoRepository> _logger;

    public SupabaseCanalIngestaoRepository(IOptions<SupabaseOptions> options, IHttpClientFactory httpClientFactory, ILogger<SupabaseCanalIngestaoRepository> logger)
    {
        this._options = options.Value;
        _logger = logger;
        _http = httpClientFactory.CreateClient("supabase");
    }

    public async Task<IReadOnlyList<CanalIngestaoConfig>> GetCanaisAtivosAsync(CancellationToken ct = default)
    {
        var response = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Get, _options.Rest("/canal_ingestao?ativo=eq.true")), ct);
        var json = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();

        var rows = JsonConvert.DeserializeObject<List<CanalIngestaoRow>>(json) ?? [];

        return rows.Select(r => new CanalIngestaoConfig(
            r.channel_id, r.playlist_id, r.nome, r.mine, r.ativo,
            ParseRelevanciaTipo(r.relevancia_tipo), r.relevancia_valores,
            r.max_videos_per_run, r.delay_between_videos_ms, r.delay_between_pages_ms,
            r.secrets_key, r.privacy_status_filtro,
            CarregarRegra(r.channel_id, r.regra_relevancia))).ToList();
    }

    /// <summary>
    /// Interpreta canal_ingestao.regra_relevancia. Sem regra (coluna nula/ausente) devolve null e o
    /// handler usa o modelo antigo. Regra inválida também devolve null — a ingestão do canal segue com a
    /// regra anterior em vez de parar — e registra Error (vira alerta no Sentry).
    /// </summary>
    private RegraRelevancia? CarregarRegra(string channelId, JToken? token)
    {
        if (token is null || token.Type == JTokenType.Null) return null;

        try
        {
            return RegraRelevancia.Parse(token.ToString(Formatting.None));
        }
        catch (RegraRelevanciaInvalidaException e)
        {
            _logger.LogError("Regra de relevância inválida no canal {ChannelId}; usando a regra anterior (enum). Motivo: {Motivo}", channelId, e.Message);
            return null;
        }
    }

    private static RelevanciaTipo ParseRelevanciaTipo(string valor) => valor switch
    {
        "live_unlisted" => RelevanciaTipo.LiveUnlisted,
        "titulo_contem" => RelevanciaTipo.TituloContem,
        _ => throw new InvalidOperationException($"relevancia_tipo desconhecido: {valor}")
    };

    private record CanalIngestaoRow(
        string channel_id, string? playlist_id, string nome, bool mine, bool ativo,
        string relevancia_tipo, List<string> relevancia_valores,
        int max_videos_per_run, int delay_between_videos_ms, int delay_between_pages_ms,
        string secrets_key, string? privacy_status_filtro = null, JToken? regra_relevancia = null);
}
