using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Ministerio.Worker;
using Giom.VideoManager.Testes.Comuns;
using Xunit;

namespace Giom.VideoManager.Application.Tests.Handlers;

/// <summary>
/// MinisterioPipelineHandler, desde a Feature #104, só cuida de ingestão — implementa IVideoHandler
/// (IsRelevant + HandleAsync publica na fila única de Transcrição, compartilhada com o Instituto).
/// O processamento da etapa de Resumo saiu daqui — vive no Resumo.Worker, testado lá.
/// </summary>
public class MinisterioPipelineHandlerTests
{
    private static Video CriarVideo(string id = "v1") => new() { Id = id, Title = "Vídeo", ChannelTitle = "Ministério Giom" };

    private sealed class Ambiente
    {
        public FakeFilaPublisher<VideoEtapaMensagem> TranscricaoPublisher { get; } = new();
        public FakeSessionContext SessionContext { get; } = new();
        public FakeTracingService Tracing { get; } = new();

        public MinisterioPipelineHandler CriarHandler() =>
            new(TranscricaoPublisher, SessionContext, Tracing);
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
}
