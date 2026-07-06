using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;

namespace Giom.VideoManager.Infrastructure.Supabase;

/// <summary>
/// Histórico de conversa persistido no Supabase (tabela conversa_historico). Sobrevive a
/// reinícios do worker — necessário se o processo reiniciar com frequência em produção.
/// Sem limite de turnos: traz a conversa inteira do chat (dentro da geração atual). Se o
/// volume de mensagens por chat crescer muito, isso aumenta o custo/tempo de cada chamada
/// ao Claude — vale revisitar com um limite (ou um resumo do histórico antigo) se isso virar
/// um problema na prática.
/// LimparAsync não apaga nada — incrementa a "geração" do chat (tabela chat_sessao). O
/// histórico antigo continua na tabela pra auditoria/depuração, só deixa de ser visível
/// pro agente porque ObterAsync/AdicionarAsync só enxergam a geração atual.
/// </summary>
public class SupabaseConversaHistoricoService : IConversaHistoricoService
{
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, int> _geracaoCache = new();

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
        var geracao = await ObterGeracaoAtualAsync(chatId, ct);
        var response = await _http.GetAsync(
            $"/rest/v1/conversa_historico?chat_id=eq.{chatId}&geracao=eq.{geracao}&select=pergunta,resposta&order=criado_em.asc", ct);

        if (!response.IsSuccessStatusCode) return [];

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonConvert.DeserializeObject<List<TurnoConversa>>(json, JsonSettings) ?? [];
    }

    public async Task<long> AdicionarAsync(string chatId, TurnoConversa turno, CancellationToken ct = default)
    {
        var geracao = await ObterGeracaoAtualAsync(chatId, ct);
        var dto = new { chat_id = chatId, pergunta = turno.Pergunta, resposta = turno.Resposta, geracao };
        var content = new StringContent(JsonConvert.SerializeObject(dto, JsonSettings), Encoding.UTF8, "application/json");

        // "Prefer: return=representation" faz o PostgREST devolver o registro criado (com o id
        // gerado pela identity column) no corpo da resposta — sem isso o POST retorna 201 vazio.
        var request = new HttpRequestMessage(HttpMethod.Post, "/rest/v1/conversa_historico") { Content = content };
        request.Headers.Add("Prefer", "return=representation");
        var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var criados = JsonConvert.DeserializeObject<List<ConversaHistoricoDto>>(json, JsonSettings);
        return criados?.FirstOrDefault()?.Id
            ?? throw new InvalidOperationException("Supabase não retornou o id do registro criado em conversa_historico.");
    }

    public async Task RegistrarFeedbackAsync(string conversaId, bool util, CancellationToken ct = default)
    {
        var dto = new { feedback = util ? 1 : -1 };
        var content = new StringContent(JsonConvert.SerializeObject(dto, JsonSettings), Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/rest/v1/conversa_historico?id=eq.{conversaId}") { Content = content };
        await _http.SendAsync(request, ct);
    }

    public async Task LimparAsync(string chatId, CancellationToken ct = default)
    {
        var atual = await ObterGeracaoAtualAsync(chatId, ct);
        var nova = atual + 1;

        var dto = new { chat_id = chatId, geracao = nova, atualizado_em = DateTime.UtcNow };
        var content = new StringContent(JsonConvert.SerializeObject(dto, JsonSettings), Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, "/rest/v1/chat_sessao") { Content = content };
        request.Headers.Add("Prefer", "resolution=merge-duplicates");
        await _http.SendAsync(request, ct);

        _geracaoCache[chatId] = nova;
    }

    private async Task<int> ObterGeracaoAtualAsync(string chatId, CancellationToken ct)
    {
        if (_geracaoCache.TryGetValue(chatId, out var cacheada)) return cacheada;

        var geracao = 1;
        var response = await _http.GetAsync($"/rest/v1/chat_sessao?chat_id=eq.{chatId}&select=geracao", ct);
        if (response.IsSuccessStatusCode)
        {
            var json = await response.Content.ReadAsStringAsync(ct);
            var sessoes = JsonConvert.DeserializeObject<List<ChatSessaoDto>>(json, JsonSettings);
            if (sessoes is { Count: > 0 }) geracao = sessoes[0].Geracao;
        }

        _geracaoCache[chatId] = geracao;
        return geracao;
    }

    private class ChatSessaoDto
    {
        public int Geracao { get; set; }
    }

    private class ConversaHistoricoDto
    {
        public long Id { get; set; }
    }
}
