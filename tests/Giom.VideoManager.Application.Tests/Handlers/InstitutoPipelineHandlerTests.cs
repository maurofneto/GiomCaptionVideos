using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Instituto.Worker;
using Giom.VideoManager.Testes.Comuns;
using Xunit;

namespace Giom.VideoManager.Application.Tests.Handlers;

/// <summary>
/// InstitutoPipelineHandler, desde a Feature #104, só cuida de ingestão — implementa IVideoHandler
/// (IsRelevant + HandleAsync publica na fila única de Transcrição, compartilhada com o
/// Ministério). O processamento de etapa (Resumo/Categorização/Embedding) saiu daqui — vive nos
/// workers de etapa próprios, testado em seus respectivos projetos de teste.
/// </summary>
public class InstitutoPipelineHandlerTests
{
    private static Video CriarVideo(string id = "v1") => new() { Id = id, Title = "Vídeo", ChannelTitle = "Instituto Giom" };

    private sealed class Ambiente
    {
        public FakeFilaPublisher<VideoEtapaMensagem> TranscricaoPublisher { get; } = new();
        public FakeSessionContext SessionContext { get; } = new();
        public FakeTracingService Tracing { get; } = new();

        public InstitutoPipelineHandler CriarHandler() =>
            new(TranscricaoPublisher, SessionContext, Tracing);
    }

    [Theory]
    [InlineData(Domain.Enums.VideoType.Live, "unlisted", true)]
    [InlineData(Domain.Enums.VideoType.Upload, "unlisted", false)]
    [InlineData(Domain.Enums.VideoType.Live, "public", false)]
    public void IsRelevant_SoAoVivoENaoListado(Domain.Enums.VideoType type, string privacyStatus, bool esperado)
    {
        var video = CriarVideo();
        video.Type = type;
        video.PrivacyStatus = privacyStatus;

        Assert.Equal(esperado, new Ambiente().CriarHandler().IsRelevant(video));
    }

    [Fact]
    public async Task HandleAsync_PublicaNaEtapaDeTranscricaoComSessaoETraceparent()
    {
        var ambiente = new Ambiente();
        var video = CriarVideo();
        var sessao = new SessaoObservabilidade("corr-1", "hora", "/caminho", "canal", new Dictionary<string, string> { ["Canal"] = "Canal X" });
        ambiente.SessionContext.Definir(sessao);
        ambiente.Tracing.ContextoASimular = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

        await ((IVideoHandler)ambiente.CriarHandler()).HandleAsync(video);

        var mensagem = Assert.Single(ambiente.TranscricaoPublisher.Publicadas);
        Assert.Same(video, mensagem.Video);
        Assert.Same(sessao, mensagem.Sessao);
        Assert.Equal("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", mensagem.TraceParent);
    }
}
