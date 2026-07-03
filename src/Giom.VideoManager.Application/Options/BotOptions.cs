namespace Giom.VideoManager.Application.Options;

/// <summary>
/// Textos de identidade do bot exibidos fora do LLM (comandos utilitários, boas-vindas).
/// Cada worker define os seus via appsettings ("Bot" section) — o mesmo ChatBotProcessor
/// serve qualquer bot, só o conteúdo muda por configuração, sem tocar em código.
/// </summary>
public class BotOptions
{
    public string MensagemBoasVindas { get; init; } = "Olá! Pode mandar sua pergunta.";
    public string MensagemSobre { get; init; } = "Bot de perguntas e respostas.";
    public string MensagemLimpar { get; init; } = "Conversa reiniciada. Pode perguntar de novo.";
}
