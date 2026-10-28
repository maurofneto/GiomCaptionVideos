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
            CarregarRegra(r.channel_id, r.regra_relevancia),
            r.max_videos_per_run, r.delay_between_videos_ms, r.delay_between_pages_ms,
            r.secrets_key)).ToList();
    }

    /// <summary>
    /// Interpreta canal_ingestao.regra_relevancia. Regra ausente ou inválida devolve
    /// <see cref="RegraRelevancia.Nenhuma"/> — nenhum vídeo do canal é considerado relevante naquela execução —
    /// e registra Error (vira alerta no Sentry). Não há regra anterior pra cair: processar vídeo errado custa
    /// tokens de IA, parar custa só esperar a correção.
    /// </summary>
    private RegraRelevancia CarregarRegra(string channelId, JToken? token)
    {
        if (token is null || token.Type == JTokenType.Null)
        {
            _logger.LogError("Canal {ChannelId} sem regra de relevância (regra_relevancia ausente); nenhum vídeo será processado nesta execução.", channelId);
            return RegraRelevancia.Nenhuma;
        }

        try
        {
            return RegraRelevancia.Parse(token.ToString(Formatting.None));
        }
        catch (RegraRelevanciaInvalidaException e)
        {
            _logger.LogError("Regra de relevância inválida no canal {ChannelId}; nenhum vídeo será processado nesta execução. Motivo: {Motivo}", channelId, e.Message);
            return RegraRelevancia.Nenhuma;
        }
    }

    private record CanalIngestaoRow(
        string channel_id, string? playlist_id, string nome, bool mine, bool ativo,
        int max_videos_per_run, int delay_between_videos_ms, int delay_between_pages_ms,
        string secrets_key, JToken? regra_relevancia = null);
}
