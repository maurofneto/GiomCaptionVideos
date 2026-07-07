using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Testes.Comuns;

/// <summary>
/// Fake em memória de IConversaHistoricoService — replica a regra real de que ObterAsync só
/// devolve turnos com Resposta preenchida (ver SupabaseConversaHistoricoService), pra que os
/// testes de ChatBotProcessor validem o comportamento de verdade (ex.: FIFO/turno em aberto),
/// não uma versão simplificada demais que deixaria passar um bug real.
/// </summary>
public class FakeConversaHistoricoService : IConversaHistoricoService
{
    private long _proximoId = 1;
    private readonly Dictionary<string, List<TurnoConversa>> _porChat = [];

    public List<(string ConversaId, bool Util)> Feedbacks { get; } = [];
    public int VezesLimpo { get; private set; }
    public int VezesAdicionado { get; private set; }
    public int VezesAtualizado { get; private set; }

    /// <summary>Injeta falha em AdicionarAsync — simula, por exemplo, o banco fora do ar antes da pergunta ser persistida.</summary>
    public bool FalharAoAdicionar { get; set; }

    /// <summary>Semeia o histórico de um chat diretamente (Resposta preenchida) pra montar cenários de teste sem passar por AdicionarAsync/AtualizarAsync.</summary>
    public void Semear(string chatId, params IEnumerable<(string Pergunta, string Resposta)> turnos)
    {
        if (!_porChat.TryGetValue(chatId, out var lista))
            _porChat[chatId] = lista = [];

        foreach (var (pergunta, resposta) in turnos)
            lista.Add(new TurnoConversa(_proximoId++, pergunta, resposta));
    }

    public Task<IReadOnlyList<TurnoConversa>> ObterAsync(string chatId, CancellationToken ct = default)
    {
        IReadOnlyList<TurnoConversa> resultado = _porChat.TryGetValue(chatId, out var turnos)
            ? turnos.Where(t => t.Resposta is not null).ToList()
            : [];

        return Task.FromResult(resultado);
    }

    public Task<TurnoConversa> AdicionarAsync(string chatId, TurnoConversa turno, CancellationToken ct = default)
    {
        if (FalharAoAdicionar) throw new InvalidOperationException("Falha simulada ao persistir a pergunta");

        VezesAdicionado++;
        var comId = turno with { Id = _proximoId++ };

        if (!_porChat.TryGetValue(chatId, out var turnos))
            _porChat[chatId] = turnos = [];

        turnos.Add(comId);
        return Task.FromResult(comId);
    }

    public Task AtualizarAsync(string chatId, TurnoConversa turno, CancellationToken ct = default)
    {
        VezesAtualizado++;

        if (_porChat.TryGetValue(chatId, out var turnos))
        {
            var index = turnos.FindIndex(t => t.Id == turno.Id);
            if (index >= 0) turnos[index] = turno;
        }

        return Task.CompletedTask;
    }

    public Task LimparAsync(string chatId, CancellationToken ct = default)
    {
        VezesLimpo++;
        _porChat.Remove(chatId);
        return Task.CompletedTask;
    }

    public Task RegistrarFeedbackAsync(string conversaId, bool util, CancellationToken ct = default)
    {
        Feedbacks.Add((conversaId, util));
        return Task.CompletedTask;
    }
}
