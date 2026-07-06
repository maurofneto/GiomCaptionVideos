namespace Giom.VideoManager.Infrastructure.Anthropic;

/// <summary>
/// Helicone é um proxy de observabilidade para chamadas de LLM — quando ApiKey está
/// configurada, redirecionamos o AnthropicClient para o gateway do Helicone (que registra
/// custo/latência/tokens de cada chamada automaticamente) em vez de chamar a Anthropic direto.
/// Opcional: se ApiKey estiver vazia, o comportamento é idêntico ao de hoje (chamada direta).
/// </summary>
public class HeliconeOptions
{
    public string? ApiKey { get; init; }

    /// <summary>
    /// Vira o header Helicone-Property-Environment — separa Dev/Prod no dashboard do Helicone
    /// via filtro de propriedade, independente de as duas keys estarem na mesma conta/projeto
    /// do Helicone ou não.
    /// </summary>
    public string? Environment { get; init; }
}
