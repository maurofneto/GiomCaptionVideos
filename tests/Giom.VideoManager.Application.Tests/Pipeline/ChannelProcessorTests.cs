using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Application.Options;
using Giom.VideoManager.Application.Pipeline;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Testes.Comuns;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Giom.VideoManager.Application.Tests.Pipeline;

/// <summary>
/// ChannelProcessor já teve dois bugs reais de paginação em produção (Board #54/#55: loop
/// infinito ao terminar a playlist, e filtro de data limitando o processamento a 1 vídeo por
/// execução) — estes testes existem especificamente pra nunca deixar essas duas classes de
/// bug voltarem sem que um teste quebre primeiro.
/// </summary>
public class ChannelProcessorTests
{
    private static readonly ChannelConfig Canal = new("UCxxxxx", null, "Canal de Teste", Mine: true);

    private static Video CriarVideo(string id, DateTime uploadedAt) => new()
    {
        Id = id,
        Title = $"Vídeo {id}",
        ChannelId = Canal.ChannelId,
        UploadedAt = uploadedAt
    };

    private sealed class Ambiente
    {
        public Mock<IVideoSourceService> VideoSource { get; } = new();
        public Mock<IProgressoRepository> Progresso { get; } = new();
        public Mock<IVideoHandler> Handler { get; } = new();
        public FakeTracingService Tracing { get; } = new();
        public FakeLogger<ChannelProcessor> Logger { get; } = new();
        public ProcessingOptions Opcoes { get; set; } = new() { DelayBetweenPagesMs = 0, DelayBetweenVideosMs = 0, MaxVideosPerRun = 100 };

        public Ambiente()
        {
            VideoSource.Setup(v => v.GetUploadPlaylistIdAsync(It.IsAny<ChannelConfig>(), It.IsAny<CancellationToken>())).ReturnsAsync("playlist-1");
            Progresso.Setup(p => p.GetLastPageTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
            Progresso.Setup(p => p.GetLastProcessedDateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((DateTime?)null);
            Progresso.Setup(p => p.SaveProgressoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            Handler.Setup(h => h.IsRelevant(It.IsAny<Video>())).Returns(true);
            Handler.Setup(h => h.HandleAsync(It.IsAny<Video>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        }

        public ChannelProcessor CriarProcessor() => new(
            VideoSource.Object, Progresso.Object, Handler.Object, Tracing, Logger, Microsoft.Extensions.Options.Options.Create(Opcoes));
    }

    [Fact]
    public async Task RunAsync_QuandoPlaylistTerminaSemProximaPagina_ParaDeBuscarNovasPaginas()
    {
        var ambiente = new Ambiente();
        var pagina = new PlaylistPage([CriarVideo("v1", DateTime.UtcNow)], NextPageToken: null);

        ambiente.VideoSource
            .Setup(v => v.GetPlaylistPageAsync("playlist-1", It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pagina);

        await ambiente.CriarProcessor().RunAsync([Canal], CancellationToken.None);

        // Sem o fix do Board #54, isso reiniciava a paginação do zero indefinidamente —
        // aqui o mock só tem UMA resposta configurada, então uma segunda chamada já quebraria
        // o teste por falta de setup; Times.Once confirma que só foi chamado 1 vez mesmo.
        ambiente.VideoSource.Verify(v => v.GetPlaylistPageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        ambiente.Handler.Verify(h => h.HandleAsync(It.Is<Video>(vd => vd.Id == "v1"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_ComMultiplosVideosNovosNaMesmaPagina_ProcessaTodosNaoSoOPrimeiro()
    {
        // Board #55: o filtro de data "congelava" errado depois do primeiro vídeo processado,
        // fazendo o resto da MESMA página (e das seguintes) ser descartado como "antigo demais".
        var ambiente = new Ambiente();
        var agora = DateTime.UtcNow;
        var pagina = new PlaylistPage(
            [CriarVideo("v1", agora), CriarVideo("v2", agora.AddMinutes(-1)), CriarVideo("v3", agora.AddMinutes(-2))],
            NextPageToken: null);

        ambiente.Progresso.Setup(p => p.GetLastProcessedDateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(agora.AddDays(-1)); // filtro de data: só ignora vídeos anteriores a ontem

        ambiente.VideoSource
            .Setup(v => v.GetPlaylistPageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pagina);

        await ambiente.CriarProcessor().RunAsync([Canal], CancellationToken.None);

        ambiente.Handler.Verify(h => h.HandleAsync(It.IsAny<Video>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task RunAsync_RespeitaMaxVideosPerRun_ParandoAntesDeEsgotarAsPaginas()
    {
        var ambiente = new Ambiente { Opcoes = new ProcessingOptions { MaxVideosPerRun = 2, DelayBetweenPagesMs = 0, DelayBetweenVideosMs = 0 } };
        var pagina = new PlaylistPage(
            [CriarVideo("v1", DateTime.UtcNow), CriarVideo("v2", DateTime.UtcNow), CriarVideo("v3", DateTime.UtcNow)],
            NextPageToken: "pagina-2");

        ambiente.VideoSource
            .Setup(v => v.GetPlaylistPageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pagina);

        await ambiente.CriarProcessor().RunAsync([Canal], CancellationToken.None);

        ambiente.Handler.Verify(h => h.HandleAsync(It.IsAny<Video>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        // Não deveria nem pedir a página 2 — o limite já foi atingido processando a página 1
        ambiente.VideoSource.Verify(v => v.GetPlaylistPageAsync(It.IsAny<string>(), It.IsAny<int>(), "pagina-2", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_PulaVideosIrrelevantesSegundoOHandler()
    {
        var ambiente = new Ambiente();
        var pagina = new PlaylistPage([CriarVideo("v1", DateTime.UtcNow), CriarVideo("v2", DateTime.UtcNow)], NextPageToken: null);
        ambiente.VideoSource.Setup(v => v.GetPlaylistPageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(pagina);
        ambiente.Handler.Setup(h => h.IsRelevant(It.Is<Video>(vd => vd.Id == "v2"))).Returns(false);

        await ambiente.CriarProcessor().RunAsync([Canal], CancellationToken.None);

        ambiente.Handler.Verify(h => h.HandleAsync(It.Is<Video>(vd => vd.Id == "v1"), It.IsAny<CancellationToken>()), Times.Once);
        ambiente.Handler.Verify(h => h.HandleAsync(It.Is<Video>(vd => vd.Id == "v2"), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_QuandoUmVideoFalha_ContinuaProcessandoOsDemaisDaMesmaPagina()
    {
        var ambiente = new Ambiente();
        var pagina = new PlaylistPage([CriarVideo("v1", DateTime.UtcNow), CriarVideo("v2", DateTime.UtcNow)], NextPageToken: null);
        ambiente.VideoSource.Setup(v => v.GetPlaylistPageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(pagina);
        ambiente.Handler.Setup(h => h.HandleAsync(It.Is<Video>(vd => vd.Id == "v1"), It.IsAny<CancellationToken>())).ThrowsAsync(new Exception("falha ao transcrever"));

        await ambiente.CriarProcessor().RunAsync([Canal], CancellationToken.None);

        ambiente.Handler.Verify(h => h.HandleAsync(It.Is<Video>(vd => vd.Id == "v2"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_QuandoUmCanalFalhaPorInteiro_ContinuaProcessandoOsDemaisCanais()
    {
        var ambiente = new Ambiente();
        var canalQueFalha = new ChannelConfig("UCfalha", null, "Canal com Erro");
        var canalOk = new ChannelConfig("UCok", null, "Canal OK");

        ambiente.VideoSource
            .Setup(v => v.GetUploadPlaylistIdAsync(It.Is<ChannelConfig>(c => c.ChannelId == "UCfalha"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("YouTube API indisponível"));
        ambiente.VideoSource
            .Setup(v => v.GetUploadPlaylistIdAsync(It.Is<ChannelConfig>(c => c.ChannelId == "UCok"), It.IsAny<CancellationToken>()))
            .ReturnsAsync("playlist-ok");
        ambiente.VideoSource
            .Setup(v => v.GetPlaylistPageAsync("playlist-ok", It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlaylistPage([CriarVideo("v1", DateTime.UtcNow)], NextPageToken: null));

        await ambiente.CriarProcessor().RunAsync([canalQueFalha, canalOk], CancellationToken.None);

        ambiente.Handler.Verify(h => h.HandleAsync(It.Is<Video>(vd => vd.Id == "v1"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_QuandoPlaylistNaoEncontrada_NaoTentaBuscarPaginas()
    {
        var ambiente = new Ambiente();
        ambiente.VideoSource.Setup(v => v.GetUploadPlaylistIdAsync(It.IsAny<ChannelConfig>(), It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);

        await ambiente.CriarProcessor().RunAsync([Canal], CancellationToken.None);

        ambiente.VideoSource.Verify(v => v.GetPlaylistPageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_AbreUmaTransacaoDeVideoPorVideo_NaoUmaSoParaAPaginaInteira()
    {
        // Bug de observabilidade real: quando a transação cobria a página inteira, cada
        // RegistrarAsync (resumo/categorização/embedding) sobrescrevia a measurement do vídeo
        // anterior com a mesma chave — o dashboard acabava mostrando o tempo do ÚLTIMO vídeo
        // processado pra "todas as etapas". Uma transação "pipeline.video" por vídeo elimina
        // esse compartilhamento.
        var ambiente = new Ambiente();
        var pagina = new PlaylistPage([CriarVideo("v1", DateTime.UtcNow), CriarVideo("v2", DateTime.UtcNow)], NextPageToken: null);
        ambiente.VideoSource.Setup(v => v.GetPlaylistPageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(pagina);

        await ambiente.CriarProcessor().RunAsync([Canal], CancellationToken.None);

        var tracesDeVideo = ambiente.Tracing.Traces.Where(t => t.Nome == "pipeline.video").ToList();
        Assert.Equal(2, tracesDeVideo.Count); // uma transação PRÓPRIA por vídeo, não uma reaproveitada
    }

    [Fact]
    public async Task RunAsync_TagueiaCadaTraceDeVideoComOIdDoVideoEOTokenDaPagina()
    {
        var ambiente = new Ambiente();
        var pagina = new PlaylistPage([CriarVideo("v1", DateTime.UtcNow)], NextPageToken: null);
        ambiente.VideoSource.Setup(v => v.GetPlaylistPageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(pagina);

        await ambiente.CriarProcessor().RunAsync([Canal], CancellationToken.None);

        var traceDeVideo = ambiente.Tracing.Traces.Single(t => t.Nome == "pipeline.video").Trace;
        Assert.Equal("v1", traceDeVideo.Tags["video.id"]);
        Assert.Equal("primeira", traceDeVideo.Tags["pagina.token"]); // primeira página = sem pageToken ainda
    }

    [Fact]
    public async Task RunAsync_TagueiaOTraceDaPaginaComOTokenAtual()
    {
        var ambiente = new Ambiente();
        var pagina = new PlaylistPage([CriarVideo("v1", DateTime.UtcNow)], NextPageToken: null);
        ambiente.Progresso.Setup(p => p.GetLastPageTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("token-retomada");
        ambiente.VideoSource.Setup(v => v.GetPlaylistPageAsync(It.IsAny<string>(), It.IsAny<int>(), "token-retomada", It.IsAny<CancellationToken>())).ReturnsAsync(pagina);

        await ambiente.CriarProcessor().RunAsync([Canal], CancellationToken.None);

        var tracePagina = ambiente.Tracing.Traces.Single(t => t.Nome == "pipeline.pagina").Trace;
        Assert.Equal("token-retomada", tracePagina.Tags["pagina.token"]);
    }

    [Fact]
    public async Task RunAsync_QuandoVideoFalha_MarcaErroNoTraceDoVideo()
    {
        var ambiente = new Ambiente();
        var pagina = new PlaylistPage([CriarVideo("v1", DateTime.UtcNow)], NextPageToken: null);
        ambiente.VideoSource.Setup(v => v.GetPlaylistPageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(pagina);
        ambiente.Handler.Setup(h => h.HandleAsync(It.IsAny<Video>(), It.IsAny<CancellationToken>())).ThrowsAsync(new Exception("falha ao transcrever"));

        await ambiente.CriarProcessor().RunAsync([Canal], CancellationToken.None);

        var traceDeVideo = ambiente.Tracing.Traces.Single(t => t.Nome == "pipeline.video").Trace;
        Assert.True(traceDeVideo.Erro);
    }

    [Fact]
    public async Task RunAsync_SalvaProgressoComOPageTokenEADataMaisRecenteProcessados()
    {
        var ambiente = new Ambiente();
        var maisRecente = DateTime.UtcNow;
        var pagina = new PlaylistPage([CriarVideo("v1", maisRecente.AddDays(-2)), CriarVideo("v2", maisRecente)], NextPageToken: null);
        ambiente.VideoSource.Setup(v => v.GetPlaylistPageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(pagina);

        await ambiente.CriarProcessor().RunAsync([Canal], CancellationToken.None);

        ambiente.Progresso.Verify(p => p.SaveProgressoAsync(
            It.IsAny<string>(), "playlist-1", null, maisRecente, It.IsAny<CancellationToken>()), Times.Once);
    }
}
