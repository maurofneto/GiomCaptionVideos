using Giom.VideoManager.Domain.Interfaces;
using System.Collections.Concurrent;

namespace Giom.VideoManager.Application.Memoria;

/// <summary>
/// Histórico em memória — some ao reiniciar o processo. Suficiente para o volume atual;
/// se o worker passar a reiniciar com frequência em produção (ex: deploy diário), trocar
/// por uma implementação persistida (ex: Supabase) sem mudar quem consome IConversaHistoricoService.
/// </summary>
public class InMemoryConversaHistoricoService : IConversaHistoricoService
{
    private const int MaxTurnos = 6;
    private readonly ConcurrentDictionary<string, List<TurnoConversa>> _historicos = new();

    public Task<IReadOnlyList<TurnoConversa>> ObterAsync(string chatId, CancellationToken ct = default)
    {
        if (!_historicos.TryGetValue(chatId, out var lista))
            return Task.FromResult<IReadOnlyList<TurnoConversa>>([]);

        lock (lista) return Task.FromResult<IReadOnlyList<TurnoConversa>>(lista.ToList());
    }

    public Task AdicionarAsync(string chatId, TurnoConversa turno, CancellationToken ct = default)
    {
        var lista = _historicos.GetOrAdd(chatId, _ => []);
        lock (lista)
        {
            lista.Add(turno);
            if (lista.Count > MaxTurnos)
                lista.RemoveAt(0);
        }
        return Task.CompletedTask;
    }

    public Task LimparAsync(string chatId, CancellationToken ct = default)
    {
        _historicos.TryRemove(chatId, out _);
        return Task.CompletedTask;
    }
}
