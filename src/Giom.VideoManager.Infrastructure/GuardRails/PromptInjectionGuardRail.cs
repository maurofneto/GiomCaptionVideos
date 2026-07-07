using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Infrastructure.GuardRails;

/// <summary>
/// Segurança: heurística por palavras-chave pra tentativas óbvias de manipular o system
/// prompt — mesmo espírito do HeuristicaIncertezaDetectorService, não é 100% precisa mas
/// pega os casos mais comuns sem precisar de um classificador de IA à parte.
/// </summary>
public class PromptInjectionGuardRail : IGuardRail
{
    private static readonly string[] SinaisInjecao =
    [
        "ignore as instruções", "ignore todas as instruções", "esqueça as instruções",
        "you are now", "ignore previous instructions", "system prompt", "modo desenvolvedor",
        "developer mode", "finja que você é", "revele seu prompt", "revele suas instruções", "jailbreak"
    ];

    public string Nome => "PromptInjection";
    public GuardRailFase Fase => GuardRailFase.Entrada;

    public Task<GuardRailResultado> AvaliarAsync(GuardRailContexto contexto, CancellationToken ct = default)
    {
        var sinal = SinaisInjecao.FirstOrDefault(s => contexto.Pergunta.Contains(s, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(sinal is null
            ? GuardRailResultado.Ok
            : GuardRailResultado.Bloquear(
                "Não posso seguir instruções desse tipo — mas fico à disposição pra tirar dúvidas sobre o conteúdo das aulas.",
                $"possível prompt injection: \"{sinal}\""));
    }
}
