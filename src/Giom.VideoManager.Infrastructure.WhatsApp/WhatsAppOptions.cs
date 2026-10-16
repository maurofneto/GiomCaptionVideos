namespace Giom.VideoManager.Infrastructure.WhatsApp;

/// <summary>
/// Configuração do canal WhatsApp via Meta Cloud API oficial (decisão do Spike #138: API
/// direta, sem BSP intermediário — mensagens de serviço dentro da janela de 24h são gratuitas,
/// e como o bot só responde a quem puxa conversa, nunca saímos dessa janela).
/// </summary>
public class WhatsAppOptions
{
    /// <summary>Id do número no Meta (não é o telefone em si) — compõe a URL de envio.</summary>
    public string PhoneNumberId { get; init; } = string.Empty;

    /// <summary>Token permanente de System User do Meta Business Manager.</summary>
    public string AccessToken { get; init; } = string.Empty;

    /// <summary>Versão da Graph API. Fixada em config pra uma atualização do Meta nunca mudar o comportamento sem deploy.</summary>
    public string ApiVersion { get; init; } = "v22.0";

    /// <summary>
    /// Segredo combinado com o Meta no cadastro do webhook — o endpoint HTTP (task #140) usa
    /// pra responder o desafio de verificação (GET hub.challenge). Fica aqui, junto do resto da
    /// config do canal, mesmo sendo consumido por lá.
    /// </summary>
    public string VerifyToken { get; init; } = string.Empty;

    /// <summary>
    /// App Secret do app no Meta — usado pelo endpoint (#140) pra validar a assinatura
    /// X-Hub-Signature-256 de cada POST recebido e recusar chamada que não veio do Meta.
    /// </summary>
    public string AppSecret { get; init; } = string.Empty;
}
