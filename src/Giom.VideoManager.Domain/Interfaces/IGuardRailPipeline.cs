namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Roda todos os IGuardRail habilitados na fase certa, em sequência, parando no primeiro que
/// bloquear. Quem orquestra (ChatBotProcessor) só conhece essa interface — nunca os guardrails
/// individuais nem a lista de quais estão habilitados.
/// </summary>
public interface IGuardRailPipeline
{
    Task<GuardRailResultado> AvaliarEntradaAsync(GuardRailContexto contexto, CancellationToken ct = default);
    Task<GuardRailResultado> AvaliarSaidaAsync(GuardRailContexto contexto, CancellationToken ct = default);
}
