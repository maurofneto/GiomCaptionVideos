using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Infrastructure.Resiliencia;

/// <summary>
/// Um elo de uma cadeia de fallback (Chain of Responsibility): tenta o próprio provider e, se
/// falhar, repassa pro próximo elo — sem laço, cada elo só conhece o seguinte. O último elo (sem
/// <see cref="ComProximo"/>) não captura a exceção, então ela propaga normalmente — preserva o
/// retry/DLQ da Feature #34 quando a cadeia inteira falha.
/// </summary>
public sealed class FallbackHandler<TResult>(string nome, Func<CancellationToken, Task<TResult>> executar)
{
    private FallbackHandler<TResult>? _proximo;

    /// <summary>Encadeia o próximo elo e o retorna, pra permitir sintaxe fluente (a.ComProximo(b).ComProximo(c)).</summary>
    public FallbackHandler<TResult> ComProximo(FallbackHandler<TResult> proximo)
    {
        _proximo = proximo;
        return proximo;
    }

    public async Task<TResult> ExecutarAsync(string etapa, ILogger logger, CancellationToken ct)
    {
        try
        {
            return await executar(ct);
        }
        catch (Exception ex) when (_proximo != null)
        {
            // LogError (não LogWarning) é deliberado: o sink Sentry (Sentry:MinimumEventLevel,
            // padrão Error) só vira Evento/Issue visível a partir daqui — reaproveita a infra já
            // ligada (Board #87/#100), sem mecanismo de alerta novo.
            logger.LogError(ex, "Fallback acionado: {Etapa} — provider {Provider} falhou, repassando pro próximo elo da cadeia", etapa, nome);
            return await _proximo!.ExecutarAsync(etapa, logger, ct);
        }
    }
}
