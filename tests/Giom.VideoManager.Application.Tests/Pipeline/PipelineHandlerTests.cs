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

    private sealed class Ambiente
    {
        public Mock<IVideoRepository> VideoRepository { get; } = new();
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
    public async Task CategorizacaoEEmbeddingInstituto_CadaUmaMarcaProcessedAtIndependente()
    {
        // Fan-out de Transcrição gera 2 ramos independentes (Resumo→Categorização e Embedding) —
        // cada um marca ProcessedAt no seu próprio término, sem coordenar com o outro (mesmo
        // comportamento que já existia nos handlers antigos, agora formalizado no orquestrador).
        var ambiente = new Ambiente();
        var videoCategorizacao = CriarVideo(ChannelIdInstituto);
        var videoEmbedding = CriarVideo(ChannelIdInstituto);
        var handler = ambiente.CriarHandler();

        await handler.ProcessarAsync(new EtapaConcluidaMensagem(videoCategorizacao, "categorizacao", null, null), CancellationToken.None);
        await handler.ProcessarAsync(new EtapaConcluidaMensagem(videoEmbedding, "embedding", null, null), CancellationToken.None);

        Assert.NotNull(videoCategorizacao.ProcessedAt);
        Assert.NotNull(videoEmbedding.ProcessedAt);
        ambiente.VideoRepository.Verify(r => r.UpsertAsync(It.IsAny<Video>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
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
    public async Task EtapaSemTopologiaConfigurada_Lanca()
    {
        var ambiente = new Ambiente();
        var video = CriarVideo(ChannelIdMinisterio);

        // Ministério não tem "categorizacao" na topologia (não usa essa etapa) — mensagem
        // inesperada, deve propagar (bug real, não deveria acontecer) em vez de ser ignorada
        // silenciosamente.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ambiente.CriarHandler().ProcessarAsync(new EtapaConcluidaMensagem(video, "categorizacao", null, null), CancellationToken.None));
    }
}
