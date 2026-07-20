using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Categorizacao.Worker;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Testes.Comuns;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Giom.VideoManager.Application.Tests.Handlers;

/// <summary>CategorizacaoEtapaHandler (Feature #104) — genérico; hoje só o Instituto publica nessa fila.</summary>
public class CategorizacaoEtapaHandlerTests
{
    private static Video CriarVideo(string id = "v1") => new() { Id = id, Title = "Vídeo", ChannelTitle = "Instituto Giom" };

    private sealed class Ambiente
    {
        public Mock<IVideoRepository> VideoRepository { get; } = new();
        public Mock<ICategorizacaoService> CategorizacaoService { get; } = new();
        public FakeFilaPublisher<EtapaConcluidaMensagem> EtapaConcluidaPublisher { get; } = new();
        public FakeSessionContext SessionContext { get; } = new();
        public FakeTracingService Tracing { get; } = new();
        public FakeLogger<CategorizacaoEtapaHandler> Logger { get; } = new();

        public CategorizacaoEtapaHandler CriarHandler()
        {
            VideoRepository.Setup(r => r.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Video?)null);

            return new CategorizacaoEtapaHandler(
                VideoRepository.Object,
                CategorizacaoService.Object,
                EtapaConcluidaPublisher,
                SessionContext,
                Tracing,
                Logger);
        }
    }

    [Fact]
    public async Task ProcessarAsync_ClassificaEPersisteEPublicaEtapaConcluida()
    {
        var ambiente = new Ambiente();
        var video = CriarVideo();
        video.Resumo = new Domain.Entities.Resumo { Descricao = "resumo" };
        ambiente.CategorizacaoService.Setup(c => c.ClassificarAsync(video, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<CategoriaTeologica>)[new CategoriaTeologica { VideoId = video.Id }]);

        await ((IEtapaCategorizacaoHandler)ambiente.CriarHandler()).ProcessarAsync(video);

        ambiente.VideoRepository.Verify(r => r.UpsertAsync(video, It.IsAny<CancellationToken>()), Times.Once);
        var evento = Assert.Single(ambiente.EtapaConcluidaPublisher.Publicadas);
        Assert.Equal("categorizacao", evento.Etapa);
    }

    [Fact]
    public async Task ProcessarAsync_SemResumo_LogaWarningExplicito()
    {
        // Bug #123: sem este log, o pulo era silencioso.
        var ambiente = new Ambiente();
        var video = CriarVideo();

        await ((IEtapaCategorizacaoHandler)ambiente.CriarHandler()).ProcessarAsync(video);

        ambiente.CategorizacaoService.Verify(c => c.ClassificarAsync(It.IsAny<Video>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains(ambiente.Logger.Entradas, e => e.Nivel == LogLevel.Warning && e.Mensagem.Contains("sem resumo", StringComparison.OrdinalIgnoreCase));
    }
}
