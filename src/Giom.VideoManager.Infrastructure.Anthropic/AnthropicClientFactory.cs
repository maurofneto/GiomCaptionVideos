using Anthropic.SDK;

namespace Giom.VideoManager.Infrastructure.Anthropic;

/// <summary>
/// Centraliza a criação do AnthropicClient para que a integração com Helicone (quando
/// configurada) não precise ser duplicada em cada Claude*Service. O AnthropicClient constrói
/// a URL da API usando ApiUrlFormat ("https://api.anthropic.com/{0}/{1}" por padrão) — trocando
/// esse valor pelo gateway do Helicone, todas as chamadas passam a ser proxeadas e registradas
/// lá, sem mudar mais nada no fluxo (o header Helicone-Auth é adicionado no HttpClient nomeado
/// "anthropic" em ServiceCollectionExtensions.AddAnthropicHttpClient).
/// </summary>
public static class AnthropicClientFactory
{
    private const string HeliconeApiUrlFormat = "https://anthropic.helicone.ai/{0}/{1}";

    public static AnthropicClient Create(string apiKey, HttpClient httpClient, HeliconeOptions? helicone)
    {
        var client = new AnthropicClient(apiKey, httpClient);
        if (!string.IsNullOrWhiteSpace(helicone?.ApiKey))
            client.ApiUrlFormat = HeliconeApiUrlFormat;
        return client;
    }
}
