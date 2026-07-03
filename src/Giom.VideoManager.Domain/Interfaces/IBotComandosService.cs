namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Capacidades utilitárias do bot, fora do fluxo de pergunta/resposta via RAG (boas-vindas,
/// ajuda, sobre, limpar histórico). Cada canal decide, do seu próprio jeito, quando invocar
/// cada uma — Telegram via comando "/limpar", outro canal via palavra-chave ou botão, por
/// exemplo — e nem todo canal precisa oferecer todas, ou nenhuma. Esta interface não conhece
/// nenhum formato de invocação, só a ação em si.
/// </summary>
public interface IBotComandosService
{
    string ObterBoasVindas();

    string ObterAjuda();

    string ObterSobre();

    /// <summary>Apaga o histórico do chat e retorna a mensagem de confirmação.</summary>
    Task<string> LimparAsync(string chatId, CancellationToken ct = default);
}
