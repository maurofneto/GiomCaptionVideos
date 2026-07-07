namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>Momento em que um guardrail atua — sobre a pergunta (antes do agente responder) ou sobre a resposta gerada (antes de enviar).</summary>
public enum GuardRailFase { Entrada, Saida }

/// <summary>
/// Verificação plugável de segurança ou confiabilidade sobre uma interação do bot. Cada
/// implementação é independente e não sabe da existência das outras — habilitar/desabilitar
/// um guardrail é só editar GuardRails:Habilitados no appsettings (ver AddGuardRails na
/// Composition), sem mudar código nem afetar os demais.
/// </summary>
public interface IGuardRail
{
    string Nome { get; }
    GuardRailFase Fase { get; }
    Task<GuardRailResultado> AvaliarAsync(GuardRailContexto contexto, CancellationToken ct = default);
}

/// <summary>Pergunta do usuário e, só na fase Saida, a resposta já gerada pelo agente.</summary>
public record GuardRailContexto(string ChatId, string Pergunta, string? Resposta = null);

/// <summary>Permitido=false interrompe a interação — RespostaSubstituta é o que o usuário recebe no lugar.</summary>
public record GuardRailResultado(bool Permitido, string? RespostaSubstituta = null, string? Motivo = null)
{
    public static readonly GuardRailResultado Ok = new(true);
    public static GuardRailResultado Bloquear(string respostaSubstituta, string motivo) => new(false, respostaSubstituta, motivo);
}
