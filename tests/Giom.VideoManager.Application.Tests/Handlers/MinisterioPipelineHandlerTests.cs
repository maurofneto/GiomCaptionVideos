using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Application.Options;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Ministerio.Worker;
using Giom.VideoManager.Testes.Comuns;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Giom.VideoManager.Application.Tests.Handlers;

/// <summary>
/// MinisterioPipelineHandler é o único handler ativo do Ministério (Feature #33) — implementa
/// IVideoHandler diretamente, dono da própria topologia de descoberta. MinisterioVideoHandler fica
/// no repositório só de histórico/rollback, não é tocado nem referenciado aqui. Só tem 2 etapas —
/// não implementa IEtapaCategorizacaoHandler/IEtapaEmbeddingHandler (ISP), diferente do Instituto.
/// </summary>
public class MinisterioPipelineHandlerTests
{
    private static Video CriarVideo(string id = "v1") => new() { Id = id, Title = "Vídeo", ChannelTitle = "Ministério Giom" };

    private sealed class Ambiente
    {
        public Mock<IVideoRepository> VideoRepository { get; } = new();
        public Mock<ICaptionService> CaptionService { get; } = new();
        public Mock<IResumoService> ResumoService { get; } = new();
        public FakeFilaPublisher<VideoEtapaMensagem> TranscricaoPublisher { get; } = new();
        public FakeFilaPublisher<VideoEtapaMensagem> ResumoPublisher { get; } = new();
        public FakeSessionContext SessionContext { get; } = new();
        public FakeTracingService Tracing { get; } = new();
        public ProcessingOptions Options { get; set; } = new() { ShouldGetCaption = true, ShouldGenerateSummary = true };

        public MinisterioPipelineHandler CriarHandler()
        {
            VideoRepository.Setup(r => r.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Video?)null);

            return new MinisterioPipelineHandler(
                VideoRepository.Object,
                CaptionService.Object,
                ResumoService.Object,
                Microsoft.Extensions.Options.Options.Create(Options),
                TranscricaoPublisher,
                ResumoPublisher,
                SessionContext,
                Tracing,
                NullLogger<MinisterioPipelineHandler>.Instance);
        }
    }

    [Theory]
    [InlineData("Culto de domingo", true)]
    [InlineData("Ministério em foco", true)]
    [InlineData("GIOM ao vivo", true)]
    [InlineData("Aula qualquer", false)]
    public void IsRelevant_TituloIndicaCultoOuMinisterio(string titulo, bool esperado)
    {
        var video = CriarVideo();
        video.Title = titulo;

        Assert.Equal(esperado, new Ambiente().CriarHandler().IsRelevant(video));
    }

    [Fact]
    public async Task HandleAsync_PublicaNaEtapaDeTranscricao()
    {
        var ambiente = new Ambiente();
        var video = CriarVideo();

        await ((IVideoHandler)ambiente.CriarHandler()).HandleAsync(video);

        var mensagem = Assert.Single(ambiente.TranscricaoPublisher.Publicadas);
        Assert.Same(video, mensagem.Video);
    }

    [Fact]
    public async Task Transcricao_QuandoResumoHabilitado_Publica()
    {
        var ambiente = new Ambiente();
        var video = CriarVideo();
        ambiente.CaptionService.Setup(c => c.GetCaptionAsync(video, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Transcricao { Conteudo = "legenda" });

        await ((IEtapaTranscricaoHandler)ambiente.CriarHandler()).ProcessarAsync(video);

        Assert.Single(ambiente.ResumoPublisher.Publicadas);
        ambiente.VideoRepository.Verify(r => r.UpsertAsync(video, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Resumo_GeraEPersisteEEhTerminal_MarcaProcessedAt()
    {
        var ambiente = new Ambiente();
        var video = CriarVideo();
        video.Transcricao = new Transcricao { Conteudo = "legenda" };
        ambiente.ResumoService.Setup(r => r.GerarResumoAsync(video, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Resumo { Descricao = "resumo" });

        await ((IEtapaResumoHandler)ambiente.CriarHandler()).ProcessarAsync(video);

        Assert.NotNull(video.ProcessedAt);
        ambiente.VideoRepository.Verify(r => r.UpsertAsync(video, It.IsAny<CancellationToken>()), Times.Once);
    }
}
