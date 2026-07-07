using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Infrastructure.GuardRails;

/// <summary>
/// Único IGuardRailPipeline registrado — recebe todos os IGuardRail habilitados via DI
/// (IEnumerable, não precisa saber quantos nem quais) e roda cada fase em sequência.
/// </summary>
public class GuardRailPipeline(IEnumerable<IGuardRail> guardRails, ILogger<GuardRailPipeline> logger) : IGuardRailPipeline
{
    public Task<GuardRailResultado> AvaliarEntradaAsync(GuardRailContexto contexto, CancellationToken ct = default)
        => AvaliarAsync(GuardRailFase.Entrada, contexto, ct);

    public Task<GuardRailResultado> AvaliarSaidaAsync(GuardRailContexto contexto, CancellationToken ct = default)
        => AvaliarAsync(GuardRailFase.Saida, contexto, ct);

    private async Task<GuardRailResultado> AvaliarAsync(GuardRailFase fase, GuardRailContexto contexto, CancellationToken ct)
    {
        foreach (var guardRail in guardRails.Where(g => g.Fase == fase))
        {
            var resultado = await guardRail.AvaliarAsync(contexto, ct);
            if (!resultado.Permitido)
            {
                logger.LogWarning("GuardRail {Nome} bloqueou {ChatId}: {Motivo}", guardRail.Nome, contexto.ChatId, resultado.Motivo);
                return resultado;
            }
        }

        return GuardRailResultado.Ok;
    }
}
