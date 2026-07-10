using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Application.Options;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Testes.Comuns;
using Giom.VideoManager.Transcricao.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Giom.VideoManager.Application.Tests.Handlers;

/// <summary>
/// TranscricaoEtapaHandler (Feature #104) — genérico de verdade, atende Instituto e Ministério
/// sem saber de canal. Diferente do antigo InstitutoPipelineHandler, não decide fan-out: sempre
/// publica EtapaConcluidaMensagem("transcricao") ao terminar, seja qual for o resultado — quem
/// decide o que vem depois é o PipelineHandler (testado separadamente).
/// </summary>
public class TranscricaoEtapaHandlerTests
{
    private static Video CriarVideo(string id = "v1") => new() { Id = id, Title = "Vídeo", ChannelTitle = "Instituto Giom" };

    private sealed class Ambiente
    {
        public Mock<IVideoRepository> VideoRepository { get; } = new();
        public Mock<ICaptionService> CaptionService { get; } = new();
        public FakeFilaPublisher<EtapaConcluidaMensagem> EtapaConcluidaPublisher { get; } = new();
        public FakeSessionContext SessionContext { get; } = new();
        public FakeTracingService Tracing { get; } = new();
        public ProcessingOptions Options { get; set; } = new() { ShouldGetCaption = true };

        public TranscricaoEtapaHandler CriarHandler()
        {
            VideoRepository.Setup(r => r.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Video?)null);

            return new TranscricaoEtapaHandler(
                VideoRepository.Object,
                CaptionService.Object,
                Microsoft.Extensions.Options.Options.Create(Options),
                EtapaConcluidaPublisher,
                SessionContext,
                Tracing,
                NullLogger<TranscricaoEtapaHandler>.Instance);
        }
    }

    [Fact]
    public async Task ProcessarAsync_ObtemLegendaEPublicaEtapaConcluida()
    {
        var ambiente = new Ambiente();
        var video = CriarVideo();
        ambiente.CaptionService.Setup(c => c.GetCaptionAsync(video, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Domain.Entities.Transcricao { Conteudo = "legenda" });

        await ((IEtapaTranscricaoHandler)ambiente.CriarHandler()).ProcessarAsync(video);

        ambiente.VideoRepository.Verify(r => r.UpsertAsync(video, It.IsAny<CancellationToken>()), Times.Once);
        var evento = Assert.Single(ambiente.EtapaConcluidaPublisher.Publicadas);
        Assert.Equal("transcricao", evento.Etapa);
        Assert.Same(video, evento.Video);
    }

    [Fact]
    public async Task ProcessarAsync_SemLegenda_AindaAssimPublicaEtapaConcluida()
    {
        // Bug #100 — vídeo sem legenda no YouTube não trava mais o pipeline: a etapa termina
        // (transcrição fica null) e avisa o orquestrador, que decide o que fazer (hoje, segue
        // fan-out normalmente; falha de conteúdo não é falha de execução).
        var ambiente = new Ambiente();
        var video = CriarVideo();
        ambiente.CaptionService.Setup(c => c.GetCaptionAsync(video, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Domain.Entities.Transcricao?)null);

        await ((IEtapaTranscricaoHandler)ambiente.CriarHandler()).ProcessarAsync(video);

        Assert.Single(ambiente.EtapaConcluidaPublisher.Publicadas);
    }

    [Fact]
    public async Task ProcessarAsync_TranscricaoJaExistente_NaoChamaCaptionServiceDeNovo()
    {
        var ambiente = new Ambiente();
        var video = CriarVideo();
        var handler = ambiente.CriarHandler();
        // Setup específico DEPOIS de CriarHandler() de propósito — CriarHandler() já registra um
        // It.IsAny<string>() genérico; Moq usa o setup mais recente quando há sobreposição, então
        // registrar antes seria mascarado pelo genérico.
        ambiente.VideoRepository.Setup(r => r.GetByIdAsync(video.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Video { Id = video.Id, Transcricao = new Domain.Entities.Transcricao { Conteudo = "já existe" } });

        await ((IEtapaTranscricaoHandler)handler).ProcessarAsync(video);

        ambiente.CaptionService.Verify(c => c.GetCaptionAsync(It.IsAny<Video>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
