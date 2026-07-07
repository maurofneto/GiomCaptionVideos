using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Testes.Comuns;
using Moq;
using Xunit;

namespace Giom.VideoManager.Application.Tests.Handlers;

/// <summary>
/// FilaVideoHandler é o IVideoHandler que ChannelProcessor de fato injeta (US #39/#40) — em vez
/// de processar, publica na fila. Estes testes garantem: IsRelevant continua delegando pro
/// processador real (mesmo filtro de hoje), e HandleAsync publica em vez de chamar
/// IVideoProcessador.HandleAsync — se isso regredir, o vídeo processaria duas vezes (uma direto,
/// outra via VideoQueueConsumer) ou nenhuma.
/// </summary>
public class FilaVideoHandlerTests
{
    private static Video CriarVideo() => new() { Id = "v1", Title = "Vídeo de Teste" };

    [Fact]
    public void IsRelevant_DelegaParaOProcessadorReal()
    {
        var processadorReal = new Mock<IVideoProcessador>();
        processadorReal.Setup(p => p.IsRelevant(It.IsAny<Video>())).Returns(true);
        var handler = new FilaVideoHandler(new FakeFilaPublisher<VideoParaProcessar>(), processadorReal.Object);

        var resultado = handler.IsRelevant(CriarVideo());

        Assert.True(resultado);
        processadorReal.Verify(p => p.IsRelevant(It.IsAny<Video>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_PublicaNaFilaEmVezDeProcessarDireto()
    {
        var processadorReal = new Mock<IVideoProcessador>();
        var publisher = new FakeFilaPublisher<VideoParaProcessar>();
        var handler = new FilaVideoHandler(publisher, processadorReal.Object);
        var video = CriarVideo();

        await handler.HandleAsync(video);

        var mensagem = Assert.Single(publisher.Publicadas);
        Assert.Same(video, mensagem.Video);
        processadorReal.Verify(p => p.HandleAsync(It.IsAny<Video>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
