using Anthropic.SDK;

namespace Giom.VideoManager.Infrastructure.Anthropic;

/// <summary>
/// Centraliza a criação do AnthropicClient para que a integração com Helicone não precise
/// ser duplicada em cada Claude*Service. O AnthropicClient constrói a URL da API usando
/// ApiUrlFormat ("https://api.anthropic.com/{0}/{1}" por padrão) — trocando esse valor pelo
/// gateway do Helicone, as chamadas passam a ser proxeadas e registradas lá.
/// useHelicone vem da opção UseHelicone de cada serviço (ClaudeAgentOptions, ClaudeResumoOptions,
/// ClaudeCategorizacaoOptions) — cada Claude*Service decide independentemente se quer passar
/// pelo Helicone, mesmo compartilhando a mesma Helicone:ApiKey e o mesmo HttpClient nomeado
/// "anthropic" (cada chamada a IHttpClientFactory.CreateClient retorna uma instância própria,
/// então adicionar o header aqui não vaza para os outros serviços do mesmo worker).
/// </summary>
public static class AnthropicClientFactory
{
    private const string HeliconeApiUrlFormat = "https://anthropic.helicone.ai/{0}/{1}";

    public static AnthropicClient Create(string apiKey, HttpClient httpClient, bool useHelicone, HeliconeOptions? helicone)
    {
        var client = new AnthropicClient(apiKey, httpClient);

        if (useHelicone && !string.IsNullOrWhiteSpace(helicone?.ApiKey))
        {
            client.ApiUrlFormat = HeliconeApiUrlFormat;
            httpClient.DefaultRequestHeaders.Add("Helicone-Auth", $"Bearer {helicone.ApiKey}");
        }

        return client;
    }
}
