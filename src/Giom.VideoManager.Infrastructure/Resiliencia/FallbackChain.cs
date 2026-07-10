namespace Giom.VideoManager.Infrastructure.Resiliencia;

/// <summary>Monta uma cadeia de <see cref="FallbackHandler{TResult}"/> a partir de uma lista ordenada — recursivo, sem laço.</summary>
public static class FallbackChain
{
    public static FallbackHandler<TResult> Montar<TResult>(
        IReadOnlyList<(string Nome, Func<CancellationToken, Task<TResult>> Executar)> elos, int indice = 0)
    {
        if (elos.Count == 0) throw new ArgumentException("Cadeia de fallback vazia", nameof(elos));

        var (nome, executar) = elos[indice];
        var handler = new FallbackHandler<TResult>(nome, executar);

        if (indice + 1 < elos.Count)
            handler.ComProximo(Montar(elos, indice + 1));

        return handler; // entrada da cadeia — quem chama só invoca ExecutarAsync nele
    }
}
