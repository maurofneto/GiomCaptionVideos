using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Testes.Comuns;

/// <summary>
/// Fake de IGuardRailPipeline — resultado configurável por fase, sem precisar registrar
/// guardrails de verdade. Grava os contextos recebidos pra assercao (ex.: confirmar que a
/// pergunta certa chegou na fase Entrada, ou a resposta certa na fase Saida).
/// </summary>
public class FakeGuardRailPipeline : IGuardRailPipeline
{
    public GuardRailResultado ResultadoEntrada { get; set; } = GuardRailResultado.Ok;
    public GuardRailResultado ResultadoSaida { get; set; } = GuardRailResultado.Ok;
    public List<GuardRailContexto> ChamadasEntrada { get; } = [];
    public List<GuardRailContexto> ChamadasSaida { get; } = [];

    public Task<GuardRailResultado> AvaliarEntradaAsync(GuardRailContexto contexto, CancellationToken ct = default)
    {
        ChamadasEntrada.Add(contexto);
        return Task.FromResult(ResultadoEntrada);
    }

    public Task<GuardRailResultado> AvaliarSaidaAsync(GuardRailContexto contexto, CancellationToken ct = default)
    {
        ChamadasSaida.Add(contexto);
        return Task.FromResult(ResultadoSaida);
    }
}
