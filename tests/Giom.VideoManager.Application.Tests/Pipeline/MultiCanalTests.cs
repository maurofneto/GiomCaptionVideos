using Giom.VideoManager.Application;
using Giom.VideoManager.Application.Options;
using Giom.VideoManager.Application.Pipeline;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Testes.Comuns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Giom.VideoManager.Application.Tests.Pipeline;

/// <summary>
/// US #24 — o bot passou a escutar mais de um canal ao mesmo tempo (Telegram e WhatsApp).
///
/// O risco que estes testes travam é de amarração: se os dois ChatBotProcessor acabarem ligados
/// ao MESMO IChatChannel, nada estoura — um canal simplesmente fica mudo, e o outro responde
/// duas vezes. Não existe erro, log nem exceção que denuncie isso; só um aluno reclamando.
/// </summary>
public class MultiCanalTests
{
    private const string CanalA = "canal-a";
    private const string CanalB = "canal-b";

    private static ServiceProvider MontarContainer(FakeChatChannel canalA, FakeChatChannel canalB)
    {
        var servicos = new ServiceCollection();

        servicos.AddLogging();
        servicos.AddChatBotProcessor(new ConfigurationBuilder().Build());

        // Dependências do ChatBotProcessor — as reais viriam da Composition; aqui entram fakes,
        // porque o que está sob teste é a AMARRAÇÃO canal <-> processor, não o miolo do bot.
        var busca = new Mock<IBuscaSemanticaService>();
        busca.Setup(e => e.BuscarAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new BuscaSemanticaResultado([], "sem contexto"));

        var agente = new Mock<IAgentService>();
        agente.Setup(a => a.ResponderAsync(It.IsAny<string>(), It.IsAny<TurnoConversa>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<TurnoConversa>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new RespostaAgente("resposta"));

        servicos.AddSingleton(busca.Object);
        servicos.AddSingleton(agente.Object);
        servicos.AddSingleton<IConversaHistoricoService>(new FakeConversaHistoricoService());
        servicos.AddSingleton<ITelemetriaService>(new FakeTelemetriaService());
        servicos.AddSingleton<ITracingService>(new FakeTracingService());
        servicos.AddSingleton<IGuardRailPipeline>(new FakeGuardRailPipeline());
        servicos.AddSingleton<IContextoTelemetria>(new FakeContextoTelemetria());

        servicos.AddKeyedSingleton<IChatChannel>(CanalA, canalA);
        servicos.AddKeyedSingleton<IChatChannel>(CanalB, canalB);

        servicos.AddChatBotProcessorDoCanal(CanalA);
        servicos.AddChatBotProcessorDoCanal(CanalB);

        return servicos.BuildServiceProvider();
    }

    [Fact]
    public void CadaCanalRegistrado_ViraUmCanalAtivoEUmProcessorProprio()
    {
        using var container = MontarContainer(new FakeChatChannel(), new FakeChatChannel());

        var ativos = container.GetServices<CanalDeChatAtivo>().Select(c => c.Chave).ToList();
        Assert.Equal([CanalA, CanalB], ativos);

        var processorA = container.GetRequiredKeyedService<ChatBotProcessor>(CanalA);
        var processorB = container.GetRequiredKeyedService<ChatBotProcessor>(CanalB);
        Assert.NotSame(processorA, processorB);
    }

    /// <summary>
    /// O teste que realmente importa: a pergunta que entrou pelo canal A tem que ser respondida
    /// NO canal A. Se a amarração estiver trocada, a resposta sai pelo canal errado — ou seja,
    /// vai parar no chat de outra pessoa.
    /// </summary>
    [Fact]
    public async Task PerguntaQueChegaPorUmCanal_ERespondidaNaqueleMesmoCanal()
    {
        var canalA = new FakeChatChannel();
        var canalB = new FakeChatChannel();
        canalA.Mensagens.Add(new IncomingMessage("chat-do-A", "quem foi Josué?", "aluno"));

        using var container = MontarContainer(canalA, canalB);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await container.GetRequiredKeyedService<ChatBotProcessor>(CanalA).RunAsync(cts.Token);
        await AguardarRespostaAsync(canalA, cts.Token);

        var resposta = Assert.Single(canalA.Respostas);
        Assert.Equal("chat-do-A", resposta.ChatId);
        Assert.Empty(canalB.Respostas);
    }

    [Fact]
    public async Task ProcessorDeUmCanal_NaoConsomeMensagemDoOutro()
    {
        var canalA = new FakeChatChannel();
        var canalB = new FakeChatChannel();
        canalB.Mensagens.Add(new IncomingMessage("chat-do-B", "pergunta do B", "aluno"));

        using var container = MontarContainer(canalA, canalB);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Roda só o processor do A — a mensagem que está no B não pode ser tocada por ele.
        await container.GetRequiredKeyedService<ChatBotProcessor>(CanalA).RunAsync(cts.Token);

        Assert.Empty(canalA.Respostas);
        Assert.Empty(canalB.Respostas);
    }

    /// <summary>
    /// RunAsync dispara o processamento sem esperar (fire-and-forget, pra chats diferentes
    /// andarem em paralelo), então a resposta chega DEPOIS que ele retorna.
    /// </summary>
    private static async Task AguardarRespostaAsync(FakeChatChannel canal, CancellationToken ct)
    {
        while (canal.Respostas.Count == 0 && !ct.IsCancellationRequested)
            await Task.Delay(25, ct);
    }
}
