namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Reenvia o voto de utilidade (👍/👎) do usuário pra uma plataforma externa de observabilidade
/// de LLM (ex.: Helicone) que já rastreia a chamada original por um id próprio — desacoplado do
/// provedor concreto, pra quem chama (ex.: o canal de chat) não precisar saber qual plataforma
/// está por trás.
/// </summary>
public interface IFeedbackExternoService
{
    Task EnviarFeedbackAsync(string requestId, bool util, CancellationToken ct = default);
}
