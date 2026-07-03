using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Net.Http.Headers;
using System.Text;

namespace Giom.VideoManager.Infrastructure.Supabase;

/// <summary>
/// Histórico de conversa persistido no Supabase (tabela conversa_historico). Sobrevive a
/// reinícios do worker — necessário se o processo reiniciar com frequência em produção.
/// Sem limite de turnos: traz a conversa inteira do chat. Se o volume de mensagens por
/// chat crescer muito, isso aumenta o custo/tempo de cada chamada ao Claude — vale revisitar
/// com um limite (ou um resumo do histórico antigo) se isso virar um problema na prática.
/// </summary>
public class SupabaseConversaHistoricoService : IConversaHistoricoService
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy() },
        NullValueHandling = NullValueHandling.Ignore
    };

    public SupabaseConversaHistoricoService(IOptions<SupabaseOptions> options, IHttpClientFactory httpClientFactory)
    {
        var sb = options.Value;
        _http = httpClientFactory.CreateClient("supabase");
        _http.BaseAddress = new Uri(sb.Url);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sb.SecretKey);
        _http.DefaultRequestHeaders.Add("apikey", sb.SecretKey);
    }

    public async Task<IReadOnlyList<TurnoConversa>> ObterAsync(string chatId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync(
            $"/rest/v1/conversa_historico?chat_id=eq.{chatId}&select=pergunta,resposta&order=criado_em.asc", ct);

        if (!response.IsSuccessStatusCode) return [];

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonConvert.DeserializeObject<List<TurnoConversa>>(json, JsonSettings) ?? [];
    }

    public async Task AdicionarAsync(string chatId, TurnoConversa turno, CancellationToken ct = default)
    {
        var dto = new { chat_id = chatId, pergunta = turno.Pergunta, resposta = turno.Resposta };
        var content = new StringContent(JsonConvert.SerializeObject(dto, JsonSettings), Encoding.UTF8, "application/json");
        await _http.PostAsync("/rest/v1/conversa_historico", content, ct);
    }

    public async Task LimparAsync(string chatId, CancellationToken ct = default)
    {
        await _http.DeleteAsync($"/rest/v1/conversa_historico?chat_id=eq.{chatId}", ct);
    }
}
