using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Application.Pipeline;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Testes.Comuns;
using Moq;
using Xunit;

namespace Giom.VideoManager.Application.Tests.Pipeline;

/// <summary>
/// VideoQueueConsumer é quem de fato chama IVideoProcessador.HandleAsync do outro lado da fila
/// (US #39/#40) — estes testes garantem que cada mensagem abre seu span "pipeline.video" (reatado
/// ao traceparent da publicação), reaplica a sessão que veio na mensagem (sem isso, os atributos
/// de Sentry/Helicone se perdiam), chama o processador real, e que um erro num vídeo não impede
/// o consumo dos próximos (a fila decide retentativa, não o consumer em si).
/// </summary>
public class VideoQueueConsumerTests
{
    private static Video CriarVideo(string id) => new() { Id = id, Title = $"Vídeo {id}", ChannelTitle = "Canal de Teste" };

    private static VideoParaProcessar CriarMensagem(Video video, SessaoObservabilidade? sessao = null, string? traceParent = null) =>
        new(video, sessao, traceParent);

    private sealed class Ambiente
    {
        public Mock<IVideoProcessador> ProcessadorReal { get; } = new();
        public FakeFilaConsumer<VideoParaProcessar> FilaConsumer { get; } = new();
        public FakeTracingService Tracing { get; } = new();
        public FakeSessionContext SessionContext { get; } = new();
        public FakeLogger<VideoQueueConsumer> Logger { get; } = new();

        public VideoQueueConsumer CriarConsumer() => new(
            FilaConsumer, ProcessadorReal.Object, Tracing, SessionContext, Logger);
    }

    [Fact]
    public async Task ConsumirAsync_ChamaProcessadorRealParaCadaMensagem()
    {
        var ambiente = new Ambiente();
        var video1 = CriarVideo("v1");
        var video2 = CriarVideo("v2");
        ambiente.FilaConsumer.MensagensParaConsumir.Add(CriarMensagem(video1));
        ambiente.FilaConsumer.MensagensParaConsumir.Add(CriarMensagem(video2));

        await ambiente.CriarConsumer().ConsumirAsync();

        ambiente.ProcessadorReal.Verify(p => p.HandleAsync(video1, It.IsAny<CancellationToken>()), Times.Once);
        ambiente.ProcessadorReal.Verify(p => p.HandleAsync(video2, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConsumirAsync_AbreUmSpanVideoProcessamentoPorMensagem()
    {
        var ambiente = new Ambiente();
        ambiente.FilaConsumer.MensagensParaConsumir.Add(CriarMensagem(CriarVideo("v1")));
        ambiente.FilaConsumer.MensagensParaConsumir.Add(CriarMensagem(CriarVideo("v2")));

        await ambiente.CriarConsumer().ConsumirAsync();

        Assert.Equal(["video.processamento", "video.processamento"], ambiente.Tracing.Traces.Select(t => t.Nome));
    }

    [Fact]
    public async Task ConsumirAsync_PassaOTraceParentDaMensagemPraReatarAArvoreDeSpans()
    {
        var ambiente = new Ambiente();
        const string traceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        ambiente.FilaConsumer.MensagensParaConsumir.Add(CriarMensagem(CriarVideo("v1"), traceParent: traceParent));

        await ambiente.CriarConsumer().ConsumirAsync();

        Assert.Equal(traceParent, ambiente.Tracing.Traces[0].Trace.ContextoExternoRecebido);
    }

    [Fact]
    public async Task ConsumirAsync_QuandoHandlerLanca_MarcaOSpanEPropagaSemPararAsOutras()
    {
        var ambiente = new Ambiente();
        var video1 = CriarVideo("v1");
        var video2 = CriarVideo("v2");
        ambiente.FilaConsumer.MensagensParaConsumir.Add(CriarMensagem(video1));
        ambiente.FilaConsumer.MensagensParaConsumir.Add(CriarMensagem(video2));

        var excecao = new InvalidOperationException("falha simulada");
        ambiente.ProcessadorReal.Setup(p => p.HandleAsync(video1, It.IsAny<CancellationToken>())).ThrowsAsync(excecao);
        ambiente.ProcessadorReal.Setup(p => p.HandleAsync(video2, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await ambiente.CriarConsumer().ConsumirAsync();

        // FakeFilaConsumer captura a exceção relançada (mesma semântica dos consumers reais:
        // a fila decide retentativa, não confirma/deleta a mensagem) — mas continua pra próxima.
        Assert.Single(ambiente.FilaConsumer.ErrosCapturados);
        Assert.Same(excecao, ambiente.FilaConsumer.ErrosCapturados[0]);
        Assert.True(ambiente.Tracing.Traces[0].Trace.Erro);
        ambiente.ProcessadorReal.Verify(p => p.HandleAsync(video2, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConsumirAsync_SemSessaoNaMensagem_DefineSessaoComCanalVideoIdETitulo()
    {
        var ambiente = new Ambiente();
        var video = CriarVideo("v1");
        ambiente.FilaConsumer.MensagensParaConsumir.Add(CriarMensagem(video));

        await ambiente.CriarConsumer().ConsumirAsync();

        var sessao = Assert.Single(ambiente.SessionContext.Definidas);
        Assert.Equal("Canal de Teste", sessao.Propriedades!["Canal"]);
        Assert.Equal("v1", sessao.Propriedades!["VideoId"]);
        Assert.Equal("Vídeo v1", sessao.Propriedades!["VideoTitle"]);
    }

    [Fact]
    public async Task ConsumirAsync_ComSessaoNaMensagem_ReaplicaAMesmaSessaoDaPublicacao()
    {
        var ambiente = new Ambiente();
        var video = CriarVideo("v1");
        var sessaoDaPublicacao = new SessaoObservabilidade(
            SessionId: "corr-1",
            SessionName: "hora",
            SessionPath: "/pipeline-dev/Canal/playlist-x/pagina-y/v1",
            UserId: "Canal",
            Propriedades: new Dictionary<string, string>
            {
                ["Environment"] = "dev",
                ["Canal"] = "Canal",
                ["Playlist"] = "x",
                ["Pagina"] = "y",
                ["VideoId"] = "v1",
                ["VideoTitle"] = "Vídeo v1"
            });
        ambiente.FilaConsumer.MensagensParaConsumir.Add(CriarMensagem(video, sessaoDaPublicacao));

        await ambiente.CriarConsumer().ConsumirAsync();

        var sessao = Assert.Single(ambiente.SessionContext.Definidas);
        Assert.Same(sessaoDaPublicacao, sessao);
        Assert.Equal("x", sessao.Propriedades!["Playlist"]);
        Assert.Equal("y", sessao.Propriedades!["Pagina"]);
    }
}
