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

    /// <summary>
    /// <paramref name="resultadoVazio"/> (Bug #134) — cobre o caso que a exceção sozinha não
    /// pegava: um provider que não lança nada, só devolve um resultado "sem conteúdo" (null,
    /// lista vazia) porque genuinamente não achou nada (ex.: YouTubeCaptionService sem legenda
    /// pro vídeo) ou porque a resposta veio truncada/malformada (ex.: Kimi com MaxTokens curto
    /// demais, ver Bug #131). Sem isso, esse resultado "vazio mas sem erro" virava a resposta
    /// FINAL da cadeia inteira — nunca chegava no próximo elo (Whisper, Claude, Grok...).
    /// Só o último elo (sem <see cref="ComProximo"/>) devolve o vazio como está — é o "ninguém
    /// da cadeia achou nada", resultado legítimo nesse ponto.
    /// </summary>
    public async Task<TResult> ExecutarAsync(string etapa, ILogger logger, CancellationToken ct, Func<TResult, bool>? resultadoVazio = null)
    {
        try
        {
            var resultado = await executar(ct);
            if (_proximo != null && resultadoVazio != null && resultadoVazio(resultado))
            {
                logger.LogWarning("Fallback acionado: {Etapa} — provider {Provider} devolveu vazio, repassando pro próximo elo da cadeia", etapa, nome);
                return await _proximo.ExecutarAsync(etapa, logger, ct, resultadoVazio);
            }
            return resultado;
        }
        catch (Exception ex) when (_proximo != null)
        {
            // LogError (não LogWarning) é deliberado: o sink Sentry (Sentry:MinimumEventLevel,
            // padrão Error) só vira Evento/Issue visível a partir daqui — reaproveita a infra já
            // ligada (Board #87/#100), sem mecanismo de alerta novo.
            logger.LogError(ex, "Fallback acionado: {Etapa} — provider {Provider} falhou, repassando pro próximo elo da cadeia", etapa, nome);
            return await _proximo!.ExecutarAsync(etapa, logger, ct, resultadoVazio);
        }
    }
}
