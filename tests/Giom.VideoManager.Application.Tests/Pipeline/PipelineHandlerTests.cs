using Giom.VideoManager.Application.Pipeline;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Testes.Comuns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Giom.VideoManager.Application.Tests.Pipeline;

/// <summary>
/// PipelineHandler (Feature #104) — orquestrador central, único (não mais duplicado por canal).
/// Só ele conhece a topologia: escuta EtapaConcluidaMensagem e decide fan-out (lista de próximas
/// etapas) ou terminal (lista vazia, marca ProcessedAt). Nenhum worker de etapa decide isso.
/// </summary>
public class PipelineHandlerTests
{
    private const string ChannelIdInstituto = "UCyyXFx2xHdzfavx6H6Q8zJw";
    private const string ChannelIdMinisterio = "UCyHMyZH5oon8jW7y_yMRstQ";

    private static Video CriarVideo(string channelId) => new() { Id = "v1", Title = "Vídeo", ChannelId = channelId, ChannelTitle = "Canal" };

    /// <summary>Simula video_etapa_terminal (bug #111) em memória — acumula por videoId as etapas terminais já registradas.</summary>
    private sealed class FakeVideoEtapaTerminalRepository : IVideoEtapaTerminalRepository
    {
        private readonly Dictionary<string, HashSet<string>> _porVideo = new();

        public Task<IReadOnlySet<string>> RegistrarConclusaoAsync(string videoId, string etapa, CancellationToken ct = default)
        {
            if (!_porVideo.TryGetValue(videoId, out var etapas))
                _porVideo[videoId] = etapas = new HashSet<string>();
            etapas.Add(etapa);
            return Task.FromResult<IReadOnlySet<string>>(etapas);
        }
    }

    /// <summary>Simula etapa_topologia em memória — conta as leituras pra provar o cache.</summary>
    private sealed class FakeTopologiaRepository : Dictionary<string, TopologiaCanal>, ITopologiaRepository
    {
        public int Leituras { get; private set; }

        public Task<TopologiaCanal?> ObterAsync(string channelId, CancellationToken ct = default)
        {
            Leituras++;
            return Task.FromResult(TryGetValue(channelId, out var topologia) ? topologia : null);
        }
    }

    private sealed class Ambiente
    {
        public Mock<IVideoRepository> VideoRepository { get; } = new();
        public FakeVideoEtapaTerminalRepository EtapaTerminalRepository { get; } = new();
        public FakeFilaPublisher<VideoEtapaMensagem> ResumoPublisher { get; } = new();
        public FakeFilaPublisher<VideoEtapaMensagem> EmbeddingPublisher { get; } = new();
        public FakeFilaPublisher<VideoEtapaMensagem> CategorizacaoPublisher { get; } = new();

        public FakeTopologiaRepository Topologias { get; } = new()
        {
            [ChannelIdInstituto] = new TopologiaCanal(new Dictionary<string, string[]>
            {
                ["transcricao"] = ["resumo", "embedding"],
                ["resumo"] = ["categorizacao"],
                ["categorizacao"] = [],
                ["embedding"] = [],
            }),
            [ChannelIdMinisterio] = new TopologiaCanal(new Dictionary<string, string[]>
            {
                ["transcricao"] = ["resumo", "embedding"],
                ["resumo"] = [],
                ["embedding"] = [],
            }),
        };

        public FakeTimeProvider Tempo { get; } = new();

        public PipelineHandler CriarHandler()
        {
            var services = new ServiceCollection();
            services.AddKeyedSingleton<IFilaPublisher<VideoEtapaMensagem>>("resumo", (_, _) => ResumoPublisher);
            services.AddKeyedSingleton<IFilaPublisher<VideoEtapaMensagem>>("embedding", (_, _) => EmbeddingPublisher);
            services.AddKeyedSingleton<IFilaPublisher<VideoEtapaMensagem>>("categorizacao", (_, _) => CategorizacaoPublisher);
            var provider = services.BuildServiceProvider();

            return new PipelineHandler(
                VideoRepository.Object,
                EtapaTerminalRepository,
                Topologias,
                provider,
                NullLogger<PipelineHandler>.Instance,
                Tempo);
        }
    }

    [Fact]
    public async Task TranscricaoConcluidaInstituto_FazFanOutPraResumoEEmbedding()
    {
        var ambiente = new Ambiente();
        var video = CriarVideo(ChannelIdInstituto);

        await ambiente.CriarHandler().ProcessarAsync(new EtapaConcluidaMensagem(video, "transcricao", null, null), CancellationToken.None);

        Assert.Single(ambiente.ResumoPublisher.Publicadas);
        Assert.Single(ambiente.EmbeddingPublisher.Publicadas);
        Assert.Empty(ambiente.CategorizacaoPublisher.Publicadas);
        ambiente.VideoRepository.Verify(r => r.UpsertAsync(It.IsAny<Video>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TranscricaoConcluidaMinisterio_FazFanOutPraResumoEEmbedding()
    {
        var ambiente = new Ambiente();
        var video = CriarVideo(ChannelIdMinisterio);

        await ambiente.CriarHandler().ProcessarAsync(new EtapaConcluidaMensagem(video, "transcricao", null, null), CancellationToken.None);

        Assert.Single(ambiente.ResumoPublisher.Publicadas);
        Assert.Single(ambiente.EmbeddingPublisher.Publicadas);
        Assert.Empty(ambiente.CategorizacaoPublisher.Publicadas);
    }

    [Fact]
    public async Task ResumoConcluidoInstituto_PublicaCategorizacao()
    {
        var ambiente = new Ambiente();
        var video = CriarVideo(ChannelIdInstituto);

        await ambiente.CriarHandler().ProcessarAsync(new EtapaConcluidaMensagem(video, "resumo", null, null), CancellationToken.None);

        Assert.Single(ambiente.CategorizacaoPublisher.Publicadas);
    }

    [Fact]
    public async Task MinisterioResumoEEmbedding_SoMarcaProcessedAtQuandoAmbosConcluem()
    {
        var ambiente = new Ambiente();
        var video = CriarVideo(ChannelIdMinisterio);
        var handler = ambiente.CriarHandler();

        await handler.ProcessarAsync(new EtapaConcluidaMensagem(video, "resumo", null, null), CancellationToken.None);
        Assert.Null(video.ProcessedAt);

        await handler.ProcessarAsync(new EtapaConcluidaMensagem(video, "embedding", null, null), CancellationToken.None);
        Assert.NotNull(video.ProcessedAt);
        ambiente.VideoRepository.Verify(r => r.UpsertAsync(video, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CategorizacaoOuEmbeddingIsolado_NaoMarcaProcessedAt_AguardaOOutroRamo()
    {
        // Bug #111: fan-out de Transcrição gera 2 ramos terminais independentes (Resumo→
        // Categorização e Embedding) — o vídeo só está de fato concluído quando AMBOS
        // terminarem, não no primeiro que chegar.
        var ambiente = new Ambiente();
        var video = CriarVideo(ChannelIdInstituto);
        var handler = ambiente.CriarHandler();

        await handler.ProcessarAsync(new EtapaConcluidaMensagem(video, "categorizacao", null, null), CancellationToken.None);

        Assert.Null(video.ProcessedAt);
        ambiente.VideoRepository.Verify(r => r.UpsertAsync(It.IsAny<Video>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CategorizacaoEEmbeddingDoMesmoVideo_SoMarcaProcessedAtQuandoAmbosConcluem()
    {
        var ambiente = new Ambiente();
        var video = CriarVideo(ChannelIdInstituto);
        var handler = ambiente.CriarHandler();

        await handler.ProcessarAsync(new EtapaConcluidaMensagem(video, "categorizacao", null, null), CancellationToken.None);
        Assert.Null(video.ProcessedAt);

        await handler.ProcessarAsync(new EtapaConcluidaMensagem(video, "embedding", null, null), CancellationToken.None);
        Assert.NotNull(video.ProcessedAt);
        ambiente.VideoRepository.Verify(r => r.UpsertAsync(video, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CategorizacaoEEmbeddingDeVideosDiferentes_CadaUmSoMarcaAoConcluirOsDoisRamosProprios()
    {
        // Ramos de vídeos diferentes não interferem entre si — o rastreamento é por videoId.
        var ambiente = new Ambiente();
        var videoA = CriarVideo(ChannelIdInstituto);
        videoA.Id = "video-a";
        var videoB = CriarVideo(ChannelIdInstituto);
        videoB.Id = "video-b";
        var handler = ambiente.CriarHandler();

        await handler.ProcessarAsync(new EtapaConcluidaMensagem(videoA, "categorizacao", null, null), CancellationToken.None);
        await handler.ProcessarAsync(new EtapaConcluidaMensagem(videoB, "embedding", null, null), CancellationToken.None);

        Assert.Null(videoA.ProcessedAt);
        Assert.Null(videoB.ProcessedAt);

        await handler.ProcessarAsync(new EtapaConcluidaMensagem(videoA, "embedding", null, null), CancellationToken.None);
        await handler.ProcessarAsync(new EtapaConcluidaMensagem(videoB, "categorizacao", null, null), CancellationToken.None);

        Assert.NotNull(videoA.ProcessedAt);
        Assert.NotNull(videoB.ProcessedAt);
    }

    [Fact]
    public async Task CanalSemTopologiaNoBanco_Lanca()
    {
        var ambiente = new Ambiente();
        var video = CriarVideo("canal-nao-mapeado");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ambiente.CriarHandler().ProcessarAsync(new EtapaConcluidaMensagem(video, "transcricao", null, null), CancellationToken.None));
    }

    [Fact]
    public async Task EtapaSemEntradaNaTopologia_TrataComoTerminal()
    {
        // Ministério não tem "categorizacao" em etapa_topologia (não usa essa etapa): etapa fora
        // da topologia do canal é tratada como terminal em vez de lançar.
        var ambiente = new Ambiente();
        var video = CriarVideo(ChannelIdMinisterio);

        await ambiente.CriarHandler().ProcessarAsync(new EtapaConcluidaMensagem(video, "categorizacao", null, null), CancellationToken.None);

        Assert.NotNull(video.ProcessedAt);
    }

    [Fact]
    public async Task TopologiaFicaEmCache_ReleSoDepoisDoTtl()
    {
        var ambiente = new Ambiente();
        var handler = ambiente.CriarHandler();

        await handler.ProcessarAsync(new EtapaConcluidaMensagem(CriarVideo(ChannelIdInstituto), "transcricao", null, null), CancellationToken.None);
        await handler.ProcessarAsync(new EtapaConcluidaMensagem(CriarVideo(ChannelIdInstituto), "transcricao", null, null), CancellationToken.None);
        Assert.Equal(1, ambiente.Topologias.Leituras);

        ambiente.Tempo.Advance(PipelineHandler.TtlTopologia + TimeSpan.FromSeconds(1));
        await handler.ProcessarAsync(new EtapaConcluidaMensagem(CriarVideo(ChannelIdInstituto), "transcricao", null, null), CancellationToken.None);
        Assert.Equal(2, ambiente.Topologias.Leituras);
    }

    [Fact]
    public async Task CanalSemEntradaNaTopologia_Lanca()
    {
        // Diferente do caso acima: aqui o canal inteiro não tem nenhuma linha em etapa_topologia —
        // isso é sempre configuração incompleta de verdade, deve propagar.
        var ambiente = new Ambiente();
        var video = CriarVideo("outro-canal");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ambiente.CriarHandler().ProcessarAsync(new EtapaConcluidaMensagem(video, "transcricao", null, null), CancellationToken.None));
    }
}
