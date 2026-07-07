using System.Collections.Concurrent;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Infrastructure.GuardRails;

public class RateLimitOptions
{
    public int MaximoPorJanela { get; init; } = 20;
    public int JanelaMinutos { get; init; } = 60;
}

/// <summary>
/// Confiabilidade/segurança: limita quantas perguntas um mesmo chat manda numa janela de
/// tempo — protege o orçamento de API de um usuário sozinho estourando o gasto. Janela
/// deslizante em memória, por instância — reinicia se o processo reiniciar, o que é aceitável
/// pra esse tipo de limite (não é contador de faturamento, que já é feito à parte).
/// </summary>
public class RateLimitGuardRail(IOptions<RateLimitOptions> options, TimeProvider? timeProvider = null) : IGuardRail
{
    // TimeProvider (não DateTime.UtcNow direto) — permite testar a janela deslizante
    // deterministicamente (avançar o relógio sem Thread.Sleep de verdade).
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _historico = new();

    public string Nome => "RateLimit";
    public GuardRailFase Fase => GuardRailFase.Entrada;

    public Task<GuardRailResultado> AvaliarAsync(GuardRailContexto contexto, CancellationToken ct = default)
    {
        var opcoes = options.Value;
        var janela = TimeSpan.FromMinutes(opcoes.JanelaMinutos);
        var agora = _timeProvider.GetUtcNow().UtcDateTime;
        var fila = _historico.GetOrAdd(contexto.ChatId, _ => new Queue<DateTime>());

        lock (fila)
        {
            while (fila.Count > 0 && agora - fila.Peek() > janela)
                fila.Dequeue();

            if (fila.Count >= opcoes.MaximoPorJanela)
            {
                return Task.FromResult(GuardRailResultado.Bloquear(
                    $"Você atingiu o limite de {opcoes.MaximoPorJanela} perguntas por {opcoes.JanelaMinutos} minutos — aguarda um pouco e manda de novo.",
                    $"{fila.Count} mensagens na janela de {opcoes.JanelaMinutos}min (limite {opcoes.MaximoPorJanela})"));
            }

            fila.Enqueue(agora);
        }

        return Task.FromResult(GuardRailResultado.Ok);
    }
}
