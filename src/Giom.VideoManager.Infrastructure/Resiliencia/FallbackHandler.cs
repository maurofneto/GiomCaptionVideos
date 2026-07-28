using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Infrastructure.Resiliencia;

/// <summary>
/// Um elo de uma cadeia de fallback (Chain of Responsibility): tenta o próprio provider e, se
/// falhar, repassa pro próximo elo — sem laço, cada elo só conhece o seguinte. O último elo (sem
/// <see cref="ComProximo"/>) não captura a exceção, então ela propaga normalmente — preserva o
/// retry/DLQ da Feature #34 quando a cadeia inteira falha.
///
/// Bug #134 — "não achei nada" (sem legenda, resposta vazia/truncada do modelo) deve lançar
/// <see cref="Giom.VideoManager.Domain.Exceptions.NenhumResultadoEncontradoException"/> em vez
/// de devolver um resultado vazio (null/lista vazia): esse tipo de retorno não é exceção, então
/// a cadeia aceitava como sucesso e nunca chegava no próximo elo (ex.: Whisper nunca tentado
/// porque YouTubeCaptionService "só" devolvia null sem legenda). Cada provider concreto é
/// responsável por lançar essa exceção no lugar certo — este handler não precisa saber a
/// diferença entre um erro técnico e um "nada encontrado", os dois usam o mesmo canal.
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
