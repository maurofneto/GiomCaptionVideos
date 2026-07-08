namespace Giom.VideoManager.Domain.Exceptions;

/// <summary>
/// Provedor externo (YouTube, Anthropic, OpenAI...) recusou por limite de uso — não é falha
/// transitória comum: retry rápido só queima tentativa (e mais cota). Cada adapter mapeia o
/// SEU formato de erro de rate limit/quota pra esta exceção de Domain (GoogleApiException
/// quotaExceeded, HTTP 429 etc.), e o consumer de fila usa <see cref="EsperaSugerida"/>
/// (Retry-After, quando o provedor informa) pra reagendar a mensagem de acordo (US #44) —
/// sem a fila precisar conhecer nenhum provedor específico.
/// </summary>
public class RateLimitExcedidoException(string origem, TimeSpan? esperaSugerida = null, Exception? innerException = null)
    : Exception(MontarMensagem(origem, esperaSugerida), innerException)
{
    public string Origem { get; } = origem;

    /// <summary>Quanto o provedor pediu pra esperar (header Retry-After) — null quando não informado (ex.: quota diária do YouTube).</summary>
    public TimeSpan? EsperaSugerida { get; } = esperaSugerida;

    private static string MontarMensagem(string origem, TimeSpan? espera) =>
        espera is { } e
            ? $"Rate limit excedido em {origem} — provedor pediu pra aguardar {e.TotalSeconds:F0}s"
            : $"Rate limit excedido em {origem}";
}
