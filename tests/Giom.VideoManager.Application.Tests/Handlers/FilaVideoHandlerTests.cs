using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Testes.Comuns;
using Moq;
using Xunit;

namespace Giom.VideoManager.Application.Tests.Handlers;

/// <summary>
/// FilaVideoHandler é o IVideoHandler que ChannelProcessor de fato injeta (US #39/#40) — em vez
/// de processar, publica na fila. Estes testes garantem: IsRelevant continua delegando pro
/// processador real (mesmo filtro de hoje), HandleAsync publica em vez de chamar
/// IVideoProcessador.HandleAsync (se regredir, o vídeo processaria duas vezes ou nenhuma), e a
/// mensagem carrega sessão + traceparent capturados no momento da publicação — sem isso, o
/// EtapaQueueConsumer da etapa Transcrição do outro lado perde atributos de Sentry/Helicone e a
/// árvore de spans.
/// </summary>
public class FilaVideoHandlerTests
{
    private static Video CriarVideo() => new() { Id = "v1", Title = "Vídeo de Teste" };

    private sealed class Ambiente
    {
        public Mock<IVideoProcessador> ProcessadorReal { get; } = new();
        public FakeFilaPublisher<VideoEtapaMensagem> Publisher { get; } = new();
        public FakeSessionContext SessionContext { get; } = new();
        public FakeTracingService Tracing { get; } = new();

        public FilaVideoHandler CriarHandler() => new(Publisher, ProcessadorReal.Object, SessionContext, Tracing);
    }

    [Fact]
    public void IsRelevant_DelegaParaOProcessadorReal()
    {
        var ambiente = new Ambiente();
        ambiente.ProcessadorReal.Setup(p => p.IsRelevant(It.IsAny<Video>())).Returns(true);

        var resultado = ambiente.CriarHandler().IsRelevant(CriarVideo());

        Assert.True(resultado);
        ambiente.ProcessadorReal.Verify(p => p.IsRelevant(It.IsAny<Video>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_PublicaNaFilaEmVezDeProcessarDireto()
    {
        var ambiente = new Ambiente();
        var video = CriarVideo();

        await ambiente.CriarHandler().HandleAsync(video);

        var mensagem = Assert.Single(ambiente.Publisher.Publicadas);
        Assert.Same(video, mensagem.Video);
        ambiente.ProcessadorReal.Verify(p => p.HandleAsync(It.IsAny<Video>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_LevaASessaoEOTraceparentAtuaisNaMensagem()
    {
        var ambiente = new Ambiente();
        var sessao = new SessaoObservabilidade("corr-1", "hora", "/caminho", "canal", new Dictionary<string, string> { ["Canal"] = "Canal X" });
        ambiente.SessionContext.Definir(sessao);
        ambiente.Tracing.ContextoASimular = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

        await ambiente.CriarHandler().HandleAsync(CriarVideo());

        var mensagem = Assert.Single(ambiente.Publisher.Publicadas);
        Assert.Same(sessao, mensagem.Sessao);
        Assert.Equal("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", mensagem.TraceParent);
    }
}
