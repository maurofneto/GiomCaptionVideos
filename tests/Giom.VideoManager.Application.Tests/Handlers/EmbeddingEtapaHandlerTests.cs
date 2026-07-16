using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Embedding.Worker;
using Giom.VideoManager.Testes.Comuns;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Giom.VideoManager.Application.Tests.Handlers;

/// <summary>EmbeddingEtapaHandler (Feature #104) — genérico; hoje só o Instituto publica nessa fila.</summary>
public class EmbeddingEtapaHandlerTests
{
    private static Video CriarVideo(string id = "v1") => new() { Id = id, Title = "Vídeo", ChannelTitle = "Instituto Giom" };

    private sealed class Ambiente
    {
        public Mock<IVideoRepository> VideoRepository { get; } = new();
        public Mock<IVideoIndexingService> EmbeddingService { get; } = new();
        public FakeFilaPublisher<EtapaConcluidaMensagem> EtapaConcluidaPublisher { get; } = new();
        public FakeSessionContext SessionContext { get; } = new();
        public FakeTracingService Tracing { get; } = new();

        public EmbeddingEtapaHandler CriarHandler()
        {
            VideoRepository.Setup(r => r.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Video?)null);

            return new EmbeddingEtapaHandler(
                VideoRepository.Object,
                EmbeddingService.Object,
                EtapaConcluidaPublisher,
                SessionContext,
                Tracing,
                NullLogger<EmbeddingEtapaHandler>.Instance);
        }
    }

    [Fact]
    public async Task ProcessarAsync_IndexaEPublicaEtapaConcluida()
    {
        var ambiente = new Ambiente();
        var video = CriarVideo();
        video.Transcricao = new Domain.Entities.Transcricao { Conteudo = "legenda" };

        await ((IEtapaEmbeddingHandler)ambiente.CriarHandler()).ProcessarAsync(video);

        ambiente.EmbeddingService.Verify(e => e.IndexarAsync(video, It.IsAny<CancellationToken>()), Times.Once);
        var evento = Assert.Single(ambiente.EtapaConcluidaPublisher.Publicadas);
        Assert.Equal("embedding", evento.Etapa);
    }
}
