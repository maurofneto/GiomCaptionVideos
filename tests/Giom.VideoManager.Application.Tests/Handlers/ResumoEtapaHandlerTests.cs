using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Resumo.Worker;
using Giom.VideoManager.Testes.Comuns;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Giom.VideoManager.Application.Tests.Handlers;

/// <summary>
/// ResumoEtapaHandler (Feature #104) — genérico, atende Instituto e Ministério com a mesma
/// credencial (unificada — antes divergia por canal). Não decide mais fan-out pra Categorização;
/// só publica EtapaConcluidaMensagem("resumo") — o orquestrador decide o resto.
/// </summary>
public class ResumoEtapaHandlerTests
{
    private static Video CriarVideo(string id = "v1") => new() { Id = id, Title = "Vídeo", ChannelTitle = "Instituto Giom" };

    private sealed class Ambiente
    {
        public Mock<IVideoRepository> VideoRepository { get; } = new();
        public Mock<IResumoService> ResumoService { get; } = new();
        public FakeFilaPublisher<EtapaConcluidaMensagem> EtapaConcluidaPublisher { get; } = new();
        public FakeSessionContext SessionContext { get; } = new();
        public FakeTracingService Tracing { get; } = new();
        public FakeLogger<ResumoEtapaHandler> Logger { get; } = new();

        public ResumoEtapaHandler CriarHandler()
        {
            VideoRepository.Setup(r => r.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Video?)null);

            return new ResumoEtapaHandler(
                VideoRepository.Object,
                ResumoService.Object,
                EtapaConcluidaPublisher,
                SessionContext,
                Tracing,
                Logger);
        }
    }

    [Fact]
    public async Task ProcessarAsync_GeraResumoEPublicaEtapaConcluida()
    {
        var ambiente = new Ambiente();
        var video = CriarVideo();
        video.Transcricao = new Domain.Entities.Transcricao { Conteudo = "legenda" };
        ambiente.ResumoService.Setup(r => r.GerarResumoAsync(video, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Domain.Entities.Resumo { Descricao = "resumo" });

        await ((IEtapaResumoHandler)ambiente.CriarHandler()).ProcessarAsync(video);

        ambiente.VideoRepository.Verify(r => r.UpsertAsync(video, It.IsAny<CancellationToken>()), Times.Once);
        var evento = Assert.Single(ambiente.EtapaConcluidaPublisher.Publicadas);
        Assert.Equal("resumo", evento.Etapa);
    }

    [Fact]
    public async Task ProcessarAsync_SemTranscricao_NaoGeraResumoMasAindaAvisaConclusao()
    {
        var ambiente = new Ambiente();
        var video = CriarVideo();

        await ((IEtapaResumoHandler)ambiente.CriarHandler()).ProcessarAsync(video);

        ambiente.ResumoService.Verify(r => r.GerarResumoAsync(It.IsAny<Video>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Single(ambiente.EtapaConcluidaPublisher.Publicadas);
    }

    [Fact]
    public async Task ProcessarAsync_SemTranscricao_LogaWarningExplicito()
    {
        // Bug #123: sem este log, o pulo era silencioso — nem WRN nem INF.
        var ambiente = new Ambiente();
        var video = CriarVideo();

        await ((IEtapaResumoHandler)ambiente.CriarHandler()).ProcessarAsync(video);

        Assert.Contains(ambiente.Logger.Entradas, e => e.Nivel == LogLevel.Warning && e.Mensagem.Contains("sem transcrição", StringComparison.OrdinalIgnoreCase));
    }
}
