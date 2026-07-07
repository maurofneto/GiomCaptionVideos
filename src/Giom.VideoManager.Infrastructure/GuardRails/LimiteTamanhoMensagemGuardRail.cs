using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Infrastructure.GuardRails;

public class LimiteTamanhoMensagemOptions
{
    public int MaximoCaracteres { get; init; } = 2000;
}

/// <summary>Confiabilidade: perguntas absurdamente longas custam mais e têm mais chance de virar abuso do que dúvida real.</summary>
public class LimiteTamanhoMensagemGuardRail(IOptions<LimiteTamanhoMensagemOptions> options) : IGuardRail
{
    public string Nome => "LimiteTamanhoMensagem";
    public GuardRailFase Fase => GuardRailFase.Entrada;

    public Task<GuardRailResultado> AvaliarAsync(GuardRailContexto contexto, CancellationToken ct = default)
    {
        var limite = options.Value.MaximoCaracteres;

        return Task.FromResult(contexto.Pergunta.Length <= limite
            ? GuardRailResultado.Ok
            : GuardRailResultado.Bloquear(
                $"Sua mensagem ficou grande demais ({contexto.Pergunta.Length} caracteres) — tenta resumir em até {limite}?",
                $"pergunta com {contexto.Pergunta.Length} caracteres (limite {limite})"));
    }
}
