using Giom.VideoManager.Domain.Interfaces;
using System.Collections.Concurrent;

namespace Giom.VideoManager.Application.Memoria;

/// <summary>
/// Histórico em memória — some ao reiniciar o processo. Suficiente para o volume atual;
/// se o worker passar a reiniciar com frequência em produção (ex: deploy diário), trocar
/// por uma implementação persistida (ex: Supabase) sem mudar quem consome IConversaHistoricoService.
/// LimparAsync não apaga a lista antiga — avança a "geração" do chat, então uma nova lista
/// vazia passa a ser usada. A lista anterior fica órfã no dicionário (aceitável aqui: é
/// memória de processo, não um registro consultável, e some no próximo restart de qualquer forma).
/// </summary>
public class InMemoryConversaHistoricoService : IConversaHistoricoService
{
    private const int MaxTurnos = 6;
    private readonly ConcurrentDictionary<string, int> _geracoes = new();
    private readonly ConcurrentDictionary<string, List<TurnoConversa>> _historicos = new();
    private long _proximoId;

    // Feedback nesta implementação é só best-effort (some ao reiniciar, igual ao resto do
    // histórico em memória) — não há um id real de linha pra atualizar, apenas registramos
    // o voto em memória para não quebrar o contrato da interface.
    private readonly ConcurrentDictionary<long, bool> _feedback = new();

    private string Chave(string chatId) => $"{chatId}:{_geracoes.GetOrAdd(chatId, 1)}";

    public Task<IReadOnlyList<TurnoConversa>> ObterAsync(string chatId, CancellationToken ct = default)
    {
        if (!_historicos.TryGetValue(Chave(chatId), out var lista))
            return Task.FromResult<IReadOnlyList<TurnoConversa>>([]);

        // Exclui um turno em andamento (pergunta gravada, resposta ainda não preenchida) do
        // contexto passado pro agente.
        lock (lista) return Task.FromResult<IReadOnlyList<TurnoConversa>>(lista.Where(t => t.Resposta != null).ToList());
    }

    public Task<TurnoConversa> AdicionarAsync(string chatId, TurnoConversa turno, CancellationToken ct = default)
    {
        var lista = _historicos.GetOrAdd(Chave(chatId), _ => []);
        var id = Interlocked.Increment(ref _proximoId);
        var turnoComId = turno with { Id = id };
        lock (lista)
        {
            lista.Add(turnoComId);
            if (lista.Count > MaxTurnos)
                lista.RemoveAt(0);
        }
        return Task.FromResult(turnoComId);
    }

    public Task AtualizarAsync(string chatId, TurnoConversa turno, CancellationToken ct = default)
    {
        if (_historicos.TryGetValue(Chave(chatId), out var lista))
        {
            lock (lista)
            {
                var indice = lista.FindIndex(t => t.Id == turno.Id);
                if (indice >= 0) lista[indice] = turno;
            }
        }
        return Task.CompletedTask;
    }

    public Task LimparAsync(string chatId, CancellationToken ct = default)
    {
        _geracoes.AddOrUpdate(chatId, 2, (_, atual) => atual + 1);
        return Task.CompletedTask;
    }

    public Task RegistrarFeedbackAsync(string conversaId, bool util, CancellationToken ct = default)
    {
        if (long.TryParse(conversaId, out var id))
            _feedback[id] = util;
        return Task.CompletedTask;
    }
}
