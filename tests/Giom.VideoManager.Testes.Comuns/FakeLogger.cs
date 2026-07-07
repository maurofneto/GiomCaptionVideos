using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Testes.Comuns;

public record EntradaDeLog(LogLevel Nivel, string Mensagem, Exception? Excecao);

/// <summary>
/// Fake de ILogger&lt;T&gt; — acumula tudo em <see cref="Entradas"/> pra assercao direta (ex.:
/// "cancelamento por mensagem nova não deveria logar Error"), sem precisar de um provider de
/// log de verdade nem de bibliotecas extras de teste de logging.
/// </summary>
public class FakeLogger<T> : ILogger<T>
{
    public List<EntradaDeLog> Entradas { get; } = [];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entradas.Add(new EntradaDeLog(logLevel, formatter(state, exception), exception));
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }
}
