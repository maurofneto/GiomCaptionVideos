using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Enums;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Testes.Comuns;
using Moq;
using Xunit;

namespace Giom.VideoManager.Application.Tests.Handlers;

/// <summary>
/// CanalIngestaoVideoHandler (US #105) — único IVideoHandler pra qualquer canal. A regra de relevância é
/// dado (#180: CanalIngestaoConfig.Regra, vinda de canal_ingestao.regra_relevancia), não uma classe por
/// canal — estes testes cobrem os comportamentos reais de hoje (Instituto: live não listada; Ministério:
/// título começa com GIOM e vídeo público) escritos como regra, e o que o handler faz com ela.
/// </summary>
public class CanalIngestaoVideoHandlerTests
{
    private const string RegraInstituto = """{"todas":[{"campo":"tipo","op":"igual","valor":"live"},{"campo":"privacidade","op":"igual","valor":"unlisted"}]}""";
    private const string RegraMinisterio = """{"todas":[{"alguma":[{"campo":"titulo","op":"comeca_com","valor":"GIOM"}]},{"campo":"privacidade","op":"igual","valor":"public"}]}""";

    private static Video CriarVideo(string id = "v1") => new() { Id = id, Title = "Vídeo", ChannelTitle = "Canal de Teste" };

    private static CanalIngestaoConfig CriarCanal(RegraRelevancia regra) => new(
        ChannelId: "UCteste", PlaylistId: null, Nome: "Canal de Teste", Mine: true, Ativo: true,
        Regra: regra, MaxVideosPerRun: 20,
        DelayBetweenVideosMs: 3000, DelayBetweenPagesMs: 1200, SecretsKey: "teste");

    private static CanalIngestaoConfig CriarCanal(string regraJson) => CriarCanal(RegraRelevancia.Parse(regraJson));

    private sealed class Ambiente
    {
        public Mock<IVideoRepository> VideoRepository { get; } = new();
        public FakeFilaPublisher<VideoEtapaMensagem> TranscricaoPublisher { get; } = new();
        public FakeSessionContext SessionContext { get; } = new();
        public FakeTracingService Tracing { get; } = new();

        public CanalIngestaoVideoHandler CriarHandler(CanalIngestaoConfig canal)
        {
            VideoRepository.Setup(r => r.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Video?)null);

            return new(canal, VideoRepository.Object, TranscricaoPublisher, SessionContext, Tracing);
        }
    }

    [Theory]
    [InlineData(VideoType.Live, "unlisted", true)]
    [InlineData(VideoType.Upload, "unlisted", false)]
    [InlineData(VideoType.Live, "public", false)]
    public void IsRelevant_Instituto_SoLiveNaoListada(VideoType type, string privacyStatus, bool esperado)
    {
        var video = CriarVideo();
        video.Type = type;
        video.PrivacyStatus = privacyStatus;

        Assert.Equal(esperado, new Ambiente().CriarHandler(CriarCanal(RegraInstituto)).IsRelevant(video));
    }

    [Theory]
    [InlineData("GIOM | Culto de domingo", "public", true)]
    [InlineData("Giom ao vivo", "public", true)]                       // sem diferenciar maiúsculas
    [InlineData("Um vídeo qualquer sobre GIOM", "public", false)]      // GIOM tem que ser o começo do título
    [InlineData("Culto de domingo", "public", false)]
    [InlineData("GIOM | Culto de domingo", "unlisted", false)]         // Ministério indexa só vídeo público
    [InlineData("GIOM | Culto de domingo", "private", false)]
    public void IsRelevant_Ministerio_TituloComecaComGiomESoPublico(string titulo, string privacyStatus, bool esperado)
    {
        var video = CriarVideo();
        video.Title = titulo;
        video.PrivacyStatus = privacyStatus;

        Assert.Equal(esperado, new Ambiente().CriarHandler(CriarCanal(RegraMinisterio)).IsRelevant(video));
    }

    [Fact]
    public void IsRelevant_RegraDinamica_IgnoraLiveAgendada()
    {
        var handler = new Ambiente().CriarHandler(CriarCanal("""
            {"todas":[{"campo":"titulo","op":"comeca_com","valor":"GIOM"},
                      {"nao":{"campo":"transmissao","op":"igual","valor":"agendada"}}]}
            """));

        var culto = CriarVideo();
        culto.Title = "GIOM | Culto";
        Assert.True(handler.IsRelevant(culto));

        var liveAgendada = CriarVideo();
        liveAgendada.Title = "GIOM | Ceia do Senhor";
        liveAgendada.Transmissao = Transmissao.Agendada;
        Assert.False(handler.IsRelevant(liveAgendada));
    }

    [Fact]
    public void IsRelevant_CanalSemRegraValida_NaoConsideraNenhumVideoRelevante()
    {
        var handler = new Ambiente().CriarHandler(CriarCanal(RegraRelevancia.Nenhuma));
        var video = CriarVideo();
        video.Type = VideoType.Live;
        video.PrivacyStatus = "unlisted";

        Assert.False(handler.IsRelevant(video));
    }

    [Fact]
    public async Task HandleAsync_PublicaNaEtapaDeTranscricaoComSessaoETraceparent()
    {
        var ambiente = new Ambiente();
        var canal = CriarCanal(RegraInstituto);
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

    [Fact]
    public async Task JaProcessadoAsync_VideoComProcessedAt_RetornaTrue()
    {
        var ambiente = new Ambiente();
        var canal = CriarCanal(RegraInstituto);
        var video = CriarVideo();
        var handler = ambiente.CriarHandler(canal);
        // Setup específico depois de CriarHandler — o catch-all (It.IsAny) montado lá dentro
        // venceria se viesse por último (Moq resolve overlaps pelo setup mais recente).
        ambiente.VideoRepository.Setup(r => r.GetByIdAsync(video.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Video { Id = video.Id, Title = video.Title, ProcessedAt = DateTime.UtcNow });

        Assert.True(await ((IVideoHandler)handler).JaProcessadoAsync(video));
    }

    [Fact]
    public async Task JaProcessadoAsync_VideoSemProcessedAt_RetornaFalse()
    {
        var ambiente = new Ambiente();
        var canal = CriarCanal(RegraInstituto);
        var video = CriarVideo();
        var handler = ambiente.CriarHandler(canal);
        ambiente.VideoRepository.Setup(r => r.GetByIdAsync(video.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Video { Id = video.Id, Title = video.Title, ProcessedAt = null });

        Assert.False(await ((IVideoHandler)handler).JaProcessadoAsync(video));
    }
}
