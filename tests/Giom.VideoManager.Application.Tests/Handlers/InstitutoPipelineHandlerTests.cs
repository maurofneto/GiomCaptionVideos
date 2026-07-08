using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Application.Options;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Instituto.Worker;
using Giom.VideoManager.Testes.Comuns;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Giom.VideoManager.Application.Tests.Handlers;

/// <summary>
/// InstitutoPipelineHandler é o orquestrador orientado a eventos do Instituto (Feature #33) — não
/// mexe em InstitutoVideoHandler (que continua servindo só IsRelevant pro FilaVideoHandler).
/// Estes testes cobrem o fan-out real das dependências entre etapas: Transcrição dispara Resumo
/// E Embedding em paralelo (Embedding não depende de Resumo), Resumo dispara Categorização,
/// Categorização/Embedding são terminais (não publicam nada).
/// </summary>
public class InstitutoPipelineHandlerTests
{
    private static Video CriarVideo(string id = "v1") => new() { Id = id, Title = "Vídeo", ChannelTitle = "Instituto Giom" };

    private sealed class Ambiente
    {
        public Mock<IVideoRepository> VideoRepository { get; } = new();
        public Mock<ICaptionService> CaptionService { get; } = new();
        public Mock<IResumoService> ResumoService { get; } = new();
        public Mock<ICategorizacaoService> CategorizacaoService { get; } = new();
        public Mock<IEmbeddingService> EmbeddingService { get; } = new();
        public FakeFilaPublisher<VideoEtapaMensagem> ResumoPublisher { get; } = new();
        public FakeFilaPublisher<VideoEtapaMensagem> CategorizacaoPublisher { get; } = new();
        public FakeFilaPublisher<VideoEtapaMensagem> EmbeddingPublisher { get; } = new();
        public FakeSessionContext SessionContext { get; } = new();
        public FakeTracingService Tracing { get; } = new();

        public ProcessingOptions Options { get; set; } = new()
        {
            ShouldGetCaption = true,
            ShouldGenerateSummary = true,
            ShouldCategorize = true,
            ShouldIndexEmbedding = true
        };

        public InstitutoPipelineHandler CriarHandler()
        {
            VideoRepository.Setup(r => r.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Video?)null);

            return new InstitutoPipelineHandler(
                VideoRepository.Object,
                CaptionService.Object,
                ResumoService.Object,
                CategorizacaoService.Object,
                EmbeddingService.Object,
                Microsoft.Extensions.Options.Options.Create(Options),
                ResumoPublisher,
                CategorizacaoPublisher,
                EmbeddingPublisher,
                SessionContext,
                Tracing,
                NullLogger<InstitutoPipelineHandler>.Instance);
        }
    }

    [Fact]
    public async Task Transcricao_QuandoResumoEEmbeddingHabilitados_FazFanOutPraOsDois()
    {
        var ambiente = new Ambiente();
        var video = CriarVideo();
        ambiente.CaptionService.Setup(c => c.GetCaptionAsync(video, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Transcricao { Conteudo = "legenda" });

        await ((IEtapaTranscricaoHandler)ambiente.CriarHandler()).ProcessarAsync(video);

        Assert.Single(ambiente.ResumoPublisher.Publicadas);
        Assert.Single(ambiente.EmbeddingPublisher.Publicadas);
        Assert.Empty(ambiente.CategorizacaoPublisher.Publicadas); // Categorização só depois do Resumo, não da Transcrição
        ambiente.VideoRepository.Verify(r => r.UpsertAsync(video, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Transcricao_QuandoSoResumoHabilitado_NaoPublicaEmbedding()
    {
        var ambiente = new Ambiente { Options = new ProcessingOptions { ShouldGetCaption = true, ShouldGenerateSummary = true, ShouldIndexEmbedding = false } };
        var video = CriarVideo();
        ambiente.CaptionService.Setup(c => c.GetCaptionAsync(video, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Transcricao { Conteudo = "legenda" });

        await ((IEtapaTranscricaoHandler)ambiente.CriarHandler()).ProcessarAsync(video);

        Assert.Single(ambiente.ResumoPublisher.Publicadas);
        Assert.Empty(ambiente.EmbeddingPublisher.Publicadas);
    }

    [Fact]
    public async Task Resumo_QuandoCategorizacaoHabilitada_PublicaProximaEtapa()
    {
        var ambiente = new Ambiente();
        var video = CriarVideo();
        video.Transcricao = new Transcricao { Conteudo = "legenda" };
        ambiente.ResumoService.Setup(r => r.GerarResumoAsync(video, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Resumo { Descricao = "resumo" });

        await ((IEtapaResumoHandler)ambiente.CriarHandler()).ProcessarAsync(video);

        Assert.Single(ambiente.CategorizacaoPublisher.Publicadas);
    }

    [Fact]
    public async Task Resumo_QuandoCategorizacaoDesabilitada_NaoPublicaNada()
    {
        var ambiente = new Ambiente { Options = new ProcessingOptions { ShouldGenerateSummary = true, ShouldCategorize = false } };
        var video = CriarVideo();
        video.Transcricao = new Transcricao { Conteudo = "legenda" };
        ambiente.ResumoService.Setup(r => r.GerarResumoAsync(video, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Resumo { Descricao = "resumo" });

        await ((IEtapaResumoHandler)ambiente.CriarHandler()).ProcessarAsync(video);

        Assert.Empty(ambiente.CategorizacaoPublisher.Publicadas);
    }

    [Fact]
    public async Task Categorizacao_ClassificaEPersisteSemPublicarNadaAlem_EEhTerminal()
    {
        var ambiente = new Ambiente();
        var video = CriarVideo();
        video.Resumo = new Resumo { Descricao = "resumo" };
        ambiente.CategorizacaoService.Setup(c => c.ClassificarAsync(video, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Domain.Entities.CategoriaTeologica>)[new Domain.Entities.CategoriaTeologica { VideoId = video.Id }]);

        await ((IEtapaCategorizacaoHandler)ambiente.CriarHandler()).ProcessarAsync(video);

        ambiente.VideoRepository.Verify(r => r.UpsertAsync(video, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Empty(ambiente.ResumoPublisher.Publicadas);
        Assert.Empty(ambiente.EmbeddingPublisher.Publicadas);
        Assert.Empty(ambiente.CategorizacaoPublisher.Publicadas);
    }

    [Fact]
    public async Task Embedding_IndexaEEhTerminal_NaoPublicaNada()
    {
        var ambiente = new Ambiente();
        var video = CriarVideo();
        video.Transcricao = new Transcricao { Conteudo = "legenda" };

        await ((IEtapaEmbeddingHandler)ambiente.CriarHandler()).ProcessarAsync(video);

        ambiente.EmbeddingService.Verify(e => e.IndexarAsync(video, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Empty(ambiente.ResumoPublisher.Publicadas);
        Assert.Empty(ambiente.CategorizacaoPublisher.Publicadas);
        Assert.Empty(ambiente.EmbeddingPublisher.Publicadas);
    }
}
