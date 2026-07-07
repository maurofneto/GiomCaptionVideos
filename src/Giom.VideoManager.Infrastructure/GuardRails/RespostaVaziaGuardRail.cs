using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Infrastructure.GuardRails;

/// <summary>Confiabilidade: rede de segurança final — se a resposta gerada vier vazia/curta demais, nunca deixa isso chegar ao usuário como está.</summary>
public class RespostaVaziaGuardRail : IGuardRail
{
    private const int MinimoCaracteres = 10;

    public string Nome => "RespostaVazia";
    public GuardRailFase Fase => GuardRailFase.Saida;

    public Task<GuardRailResultado> AvaliarAsync(GuardRailContexto contexto, CancellationToken ct = default)
    {
        var resposta = (contexto.Resposta ?? string.Empty).Trim();

        return Task.FromResult(resposta.Length >= MinimoCaracteres
            ? GuardRailResultado.Ok
            : GuardRailResultado.Bloquear(
                "Desculpe, não consegui gerar uma resposta agora. Tente novamente em instantes.",
                $"resposta com {resposta.Length} caracteres (mínimo {MinimoCaracteres})"));
    }
}
