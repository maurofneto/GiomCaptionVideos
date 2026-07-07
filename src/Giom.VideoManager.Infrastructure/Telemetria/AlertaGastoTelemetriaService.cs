using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Infrastructure.Telemetria;

public class AlertaGastoOptions
{
    public bool Habilitado { get; init; } = false;

    /// <summary>Gasto estimado acumulado no dia (UTC), em USD, que dispara o alerta.</summary>
    public decimal LimiteDiarioUsd { get; init; } = 5.0m;
}

/// <summary>
/// Sink de telemetria que só acumula custo — não persiste nada. Ao ultrapassar o limite diário
/// configurado, loga em nível Critical, que o sink Sentry (já plugado no CompositeTelemetriaService)
/// transforma automaticamente num alerta visível, sem precisar de nenhuma infraestrutura nova.
/// Um disparo por dia (UTC) é suficiente — não faz sentido reenviar o mesmo alerta a cada chamada
/// de IA subsequente.
/// </summary>
public class AlertaGastoTelemetriaService(IOptions<AlertaGastoOptions> options, ILogger<AlertaGastoTelemetriaService> logger, TimeProvider? timeProvider = null) : ITelemetriaService
{
    // TimeProvider (não DateTime.UtcNow direto) — permite testar a virada de dia/reset do
    // alerta deterministicamente (ver FakeTimeProvider nos testes), sem depender do relógio
    // real nem de Thread.Sleep.
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly object _lock = new();
    private DateOnly? _diaAtual;
    private decimal _acumuladoDia;
    private bool _alertaDisparadoHoje;

    public Task RegistrarAsync(RegistroTelemetria registro, CancellationToken ct = default)
    {
        var opcoes = options.Value;
        if (!opcoes.Habilitado || registro.CustoEstimadoUsd is not { } custo)
            return Task.CompletedTask;

        lock (_lock)
        {
            var hoje = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
            if (hoje != _diaAtual)
            {
                _diaAtual = hoje;
                _acumuladoDia = 0;
                _alertaDisparadoHoje = false;
            }

            _acumuladoDia += custo;

            if (!_alertaDisparadoHoje && _acumuladoDia >= opcoes.LimiteDiarioUsd)
            {
                _alertaDisparadoHoje = true;
                logger.LogCritical(
                    "Alerta de gasto: custo estimado acumulado hoje (${Acumulado:F2}) atingiu o limite configurado (${Limite:F2})",
                    _acumuladoDia, opcoes.LimiteDiarioUsd);
            }
        }

        return Task.CompletedTask;
    }
}
