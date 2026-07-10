using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Enums;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Testes.Comuns;
using Xunit;

namespace Giom.VideoManager.Application.Tests.Handlers;

/// <summary>
/// CanalIngestaoVideoHandler (US #105) — único IVideoHandler pra qualquer canal, substituindo
/// InstitutoPipelineHandler/MinisterioPipelineHandler (que ficam por enquanto, retirados só no
/// cutover final). A regra de relevância vira dado (RelevanciaTipo + RelevanciaValores), não
/// mais uma classe por canal — estes testes cobrem os mesmos 2 comportamentos reais de hoje
/// (Instituto: Live+unlisted; Ministério: título contém/começa com termos), agora orientados a
/// dado em vez de hardcoded em código.
/// </summary>
public class CanalIngestaoVideoHandlerTests
{
    private static Video CriarVideo(string id = "v1") => new() { Id = id, Title = "Vídeo", ChannelTitle = "Canal de Teste" };

    private static CanalIngestaoConfig CriarCanal(RelevanciaTipo tipo, params string[] valores) => new(
        ChannelId: "UCteste", PlaylistId: null, Nome: "Canal de Teste", Mine: true, Ativo: true,
        RelevanciaTipo: tipo, RelevanciaValores: valores, MaxVideosPerRun: 20,
        DelayBetweenVideosMs: 3000, DelayBetweenPagesMs: 1200, SecretsKey: "teste");

    private sealed class Ambiente
    {
        public FakeFilaPublisher<VideoEtapaMensagem> TranscricaoPublisher { get; } = new();
        public FakeSessionContext SessionContext { get; } = new();
        public FakeTracingService Tracing { get; } = new();

        public CanalIngestaoVideoHandler CriarHandler(CanalIngestaoConfig canal) =>
            new(canal, TranscricaoPublisher, SessionContext, Tracing);
    }

    [Theory]
    [InlineData(VideoType.Live, "unlisted", true)]
    [InlineData(VideoType.Upload, "unlisted", false)]
    [InlineData(VideoType.Live, "public", false)]
    public void IsRelevant_LiveUnlisted_SoAoVivoENaoListado(VideoType type, string privacyStatus, bool esperado)
    {
        var canal = CriarCanal(RelevanciaTipo.LiveUnlisted);
        var video = CriarVideo();
        video.Type = type;
        video.PrivacyStatus = privacyStatus;

        Assert.Equal(esperado, new Ambiente().CriarHandler(canal).IsRelevant(video));
    }

    [Theory]
    [InlineData("Culto de domingo", true)]
    [InlineData("Ministério em foco", true)]
    [InlineData("GIOM ao vivo", true)]
    [InlineData("Um vídeo qualquer sobre GIOM", false)] // "GIOM*" é prefixo, não substring
    [InlineData("Aula qualquer", false)]
    public void IsRelevant_TituloContem_ContainsParaValorSemAsterisco_StartsWithParaValorComAsterisco(string titulo, bool esperado)
    {
        var canal = CriarCanal(RelevanciaTipo.TituloContem, "CULTO", "MINISTÉRIO", "GIOM*");
        var video = CriarVideo();
        video.Title = titulo;

        Assert.Equal(esperado, new Ambiente().CriarHandler(canal).IsRelevant(video));
    }

    [Fact]
    public async Task HandleAsync_PublicaNaEtapaDeTranscricaoComSessaoETraceparent()
    {
        var ambiente = new Ambiente();
        var canal = CriarCanal(RelevanciaTipo.LiveUnlisted);
        var video = CriarVideo();
        var sessao = new SessaoObservabilidade("corr-1", "hora", "/caminho", "canal", new Dictionary<string, string> { ["Canal"] = "Canal X" });
        ambiente.SessionContext.Definir(sessao);
        ambiente.Tracing.ContextoASimular = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

        await ((IVideoHandler)ambiente.CriarHandler(canal)).HandleAsync(video);

        var mensagem = Assert.Single(ambiente.TranscricaoPublisher.Publicadas);
        Assert.Same(video, mensagem.Video);
        Assert.Same(sessao, mensagem.Sessao);
        Assert.Equal("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", mensagem.TraceParent);
    }
}
