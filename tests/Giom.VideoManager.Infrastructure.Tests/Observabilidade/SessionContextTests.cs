using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Observabilidade;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.Observabilidade;

public class SessionContextTests
{
    [Fact]
    public void Atual_AntesDeQualquerDefinir_EhNulo()
    {
        var contexto = new SessionContext();

        Assert.Null(contexto.Atual);
    }

    [Fact]
    public void Definir_DepoisAtualDevolveAMesmaSessao()
    {
        var contexto = new SessionContext();
        var sessao = new SessaoObservabilidade("session-1", "Bot Instituto Giom", "/bot/pergunta", UserId: "chat-1");

        contexto.Definir(sessao);

        Assert.Same(sessao, contexto.Atual);
    }

    [Fact]
    public void Definir_ChamadoDeNovo_SubstituiASessaoAnterior()
    {
        var contexto = new SessionContext();
        contexto.Definir(new SessaoObservabilidade("session-1", "Nome", "/path"));
        var segunda = new SessaoObservabilidade("session-2", "Nome", "/path");

        contexto.Definir(segunda);

        Assert.Same(segunda, contexto.Atual);
    }

    [Fact]
    public async Task EscoposDeFluxosAssincronosDiferentes_NaoSeMisturam()
    {
        var contexto = new SessionContext();

        var tarefaA = Task.Run(async () =>
        {
            contexto.Definir(new SessaoObservabilidade("session-A", "Nome", "/path"));
            await Task.Delay(30);
            return contexto.Atual;
        });

        var tarefaB = Task.Run(async () =>
        {
            contexto.Definir(new SessaoObservabilidade("session-B", "Nome", "/path"));
            await Task.Delay(10);
            return contexto.Atual;
        });

        var sessaoA = await tarefaA;
        var sessaoB = await tarefaB;

        Assert.Equal("session-A", sessaoA?.SessionId);
        Assert.Equal("session-B", sessaoB?.SessionId);
    }
}
