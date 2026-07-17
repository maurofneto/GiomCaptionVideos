namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>Nomes de dimensão usados por <see cref="IFarolRateLimiter"/> — o provedor externo aplica cada uma independente (ex: Voyage devolve "3 RPM e 10K TPM" na mesma mensagem de erro), então cada uma tem seu próprio bucket, nunca um genérico só chamado "tokens".</summary>
public static class RateLimiterDimensao
{
    public const string Requisicoes = "requisicoes";
    public const string Tokens = "tokens";

    /// <summary>
    /// Cohere Embed não limita por request nem por token — limita por "inputs" (nº de textos no
    /// batch), 2000/min tanto no trial quanto em produção, documentado à parte do RPM genérico
    /// de "Default" (que não se aplica ao endpoint Embed). Unidade própria, não confundir com
    /// Requisicoes nem Tokens.
    /// </summary>
    public const string Inputs = "inputs";
}

/// <summary>
/// Farol de rate limit (Bug #122) — token bucket compartilhado por provedor externo (Voyage,
/// Cohere, etc.), coordenando TODAS as tasks concorrentes do mesmo Service. Cada task ECS é um
/// processo separado sem estado compartilhado — sem isso, N tasks rodando ao mesmo tempo
/// (autoscaling até maxCapacity) disparam requisições simultâneas contra o mesmo limite do
/// provedor, estourando 429 mesmo com retry. O controle precisa viver fora de cada processo; a
/// implementação (Infrastructure) decide onde (hoje: Postgres, via SELECT...FOR UPDATE).
/// </summary>
public interface IFarolRateLimiter
{
    /// <summary>
    /// Bloqueia (poll com backoff) até o farol autorizar TODAS as dimensões pedidas de uma vez
    /// (ex: {Requisicoes: 1, Tokens: 4200}) — tudo ou nada: nunca debita só uma dimensão e fica
    /// preso esperando outra com o crédito já gasto. Deixa a escala horizontal (maxCapacity > 1)
    /// segura sem reduzir a capacidade de processamento: o gargalo real é o provedor externo,
    /// não a nossa infra, então esperar aqui não perde throughput de verdade.
    /// </summary>
    Task AguardarAsync(string provedor, IReadOnlyDictionary<string, double> consumo, CancellationToken ct = default);
}
