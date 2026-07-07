using Giom.VideoManager.Infrastructure.GuardRails;
using Microsoft.Extensions.Options;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.GuardRails;

public class AllowlistControleAcessoServiceTests
{
    private static AllowlistControleAcessoService CriarServico(params string[] chatIdsAutorizados) =>
        new(Options.Create(new AllowlistControleAcessoOptions { ChatIdsAutorizados = chatIdsAutorizados }));

    [Fact]
    public async Task EstaAutorizadoAsync_ChatIdNaLista_RetornaTrue()
    {
        var servico = CriarServico("chat-1", "chat-2");

        Assert.True(await servico.EstaAutorizadoAsync("chat-1"));
    }

    [Fact]
    public async Task EstaAutorizadoAsync_ChatIdForaDaLista_RetornaFalse()
    {
        var servico = CriarServico("chat-1", "chat-2");

        Assert.False(await servico.EstaAutorizadoAsync("chat-3"));
    }

    [Fact]
    public async Task EstaAutorizadoAsync_ListaVazia_NuncaAutoriza()
    {
        var servico = CriarServico();

        Assert.False(await servico.EstaAutorizadoAsync("qualquer-chat"));
    }
}
