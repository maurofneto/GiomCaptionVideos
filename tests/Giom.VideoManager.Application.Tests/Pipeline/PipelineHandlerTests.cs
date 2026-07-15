using Giom.VideoManager.Application.Options;
using Giom.VideoManager.Application.Pipeline;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Testes.Comuns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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

    private sealed class Ambiente
    {
        public Mock<IVideoRepository> VideoRepository { get; } = new();
        public FakeVideoEtapaTerminalRepository EtapaTerminalRepository { get; } = new();
        public FakeFilaPublisher<VideoEtapaMensagem> ResumoPublisher { get; } = new();
        public FakeFilaPublisher<VideoEtapaMensagem> EmbeddingPublisher { get; } = new();
        public FakeFilaPublisher<VideoEtapaMensagem> CategorizacaoPublisher { get; } = new();

        public TopologiaOptions Topologia { get; } = new()
        {
            Canais = new Dictionary<string, string>
            {
                [ChannelIdInstituto] = "Instituto",
                [ChannelIdMinisterio] = "Ministerio",
            },
            Topologia = new Dictionary<string, Dictionary<string, string[]>>
            {
                ["Instituto"] = new()
                {
                    ["transcricao"] = ["resumo", "embedding"],
                    ["resumo"] = ["categorizacao"],
                    ["categorizacao"] = [],
                    ["embedding"] = [],
                },
                ["Ministerio"] = new()
                {
                    ["transcricao"] = ["resumo"],
                    ["resumo"] = [],
                },
            },
        };

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
                Microsoft.Extensions.Options.Options.Create(Topologia),
                provider,
                NullLogger<PipelineHandler>.Instance);
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
    public async Task TranscricaoConcluidaMinisterio_PublicaSoResumo()
    {
        var ambiente = new Ambiente();
        var video = CriarVideo(ChannelIdMinisterio);

        await ambiente.CriarHandler().ProcessarAsync(new EtapaConcluidaMensagem(video, "transcricao", null, null), CancellationToken.None);

        Assert.Single(ambiente.ResumoPublisher.Publicadas);
        Assert.Empty(ambiente.EmbeddingPublisher.Publicadas);
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
    public async Task ResumoConcluidoMinisterio_ETerminal_MarcaProcessedAt()
    {
        var ambiente = new Ambiente();
        var video = CriarVideo(ChannelIdMinisterio);

        await ambiente.CriarHandler().ProcessarAsync(new EtapaConcluidaMensagem(video, "resumo", null, null), CancellationToken.None);

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
    public async Task CanalDesconhecido_Lanca()
    {
        var ambiente = new Ambiente();
        var video = CriarVideo("canal-nao-mapeado");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ambiente.CriarHandler().ProcessarAsync(new EtapaConcluidaMensagem(video, "transcricao", null, null), CancellationToken.None));
    }

    [Fact]
    public async Task EtapaSemEntradaNaTopologia_TrataComoTerminal()
    {
        // Ministério não tem "categorizacao" na topologia (não usa essa etapa) — mesmo caso que
        // uma etapa com "[]" explícito no JSON: o binder do IConfiguration não distingue "chave
        // ausente" de "array vazio" (nenhum dos dois sobra na flatten), então o orquestrador
        // trata os dois como terminal em vez de lançar.
        var ambiente = new Ambiente();
        var video = CriarVideo(ChannelIdMinisterio);

        await ambiente.CriarHandler().ProcessarAsync(new EtapaConcluidaMensagem(video, "categorizacao", null, null), CancellationToken.None);

        Assert.NotNull(video.ProcessedAt);
    }

    [Fact]
    public async Task CanalSemEntradaNaTopologia_Lanca()
    {
        // Diferente do caso acima: aqui o canal inteiro está ausente de Topologia:Topologia
        // (não é uma questão de array vazio vs. chave ausente numa sub-seção) — isso é sempre
        // configuração incompleta de verdade, deve propagar.
        var ambiente = new Ambiente();
        ambiente.Topologia.Canais["outro-canal"] = "SemTopologia";
        var video = CriarVideo("outro-canal");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ambiente.CriarHandler().ProcessarAsync(new EtapaConcluidaMensagem(video, "transcricao", null, null), CancellationToken.None));
    }
}
