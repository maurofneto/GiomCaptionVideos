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
}
