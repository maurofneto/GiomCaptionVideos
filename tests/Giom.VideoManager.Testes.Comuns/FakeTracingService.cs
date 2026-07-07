using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Testes.Comuns;

/// <summary>Fake de ITrace — só registra os nomes das etapas abertas e se MarcarErro foi chamado.</summary>
public class FakeTrace : ITrace
{
    public List<string> Etapas { get; } = [];
    public Dictionary<string, string> Tags { get; } = [];
    public bool Erro { get; private set; }
    public Exception? ExcecaoMarcada { get; private set; }

    public IDisposable IniciarEtapa(string nome)
    {
        Etapas.Add(nome);
        return NoopDisposable.Instance;
    }

    public void MarcarErro(Exception ex)
    {
        Erro = true;
        ExcecaoMarcada = ex;
    }

    public void DefinirTag(string chave, string valor) => Tags[chave] = valor;

    public void Dispose() { }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}

/// <summary>Fake de ITracingService — cada IniciarTrace devolve um FakeTrace novo, todos guardados em <see cref="Traces"/>.</summary>
public class FakeTracingService : ITracingService
{
    public List<(string Nome, string Operacao, FakeTrace Trace)> Traces { get; } = [];
    public List<string> EtapasAtuais { get; } = [];

    public ITrace IniciarTrace(string nome, string operacao)
    {
        var trace = new FakeTrace();
        Traces.Add((nome, operacao, trace));
        return trace;
    }

    public IDisposable IniciarEtapaAtual(string nome)
    {
        EtapasAtuais.Add(nome);
        return NoopDisposableEtapaAtual.Instance;
    }

    private sealed class NoopDisposableEtapaAtual : IDisposable
    {
        public static readonly NoopDisposableEtapaAtual Instance = new();
        public void Dispose() { }
    }
}
