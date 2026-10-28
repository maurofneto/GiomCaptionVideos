using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Application.Options;
using Giom.VideoManager.Application.Pipeline;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Enums;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Testes.Comuns;
using Microsoft.Extensions.Hosting;
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
        public Mock<IVideoRepository> VideoRepo { get; } = new();
        public FakeTracingService Tracing { get; } = new();
        public FakeSessionContext SessionContext { get; } = new();
        public FakeActiveChannelContext ActiveChannel { get; } = new();
        public Mock<IHostEnvironment> HostEnvironment { get; } = new();
        public FakeLogger<ChannelProcessor> Logger { get; } = new();
        public ProcessingOptions Opcoes { get; set; } = new() { DelayBetweenPagesMs = 0, DelayBetweenVideosMs = 0, MaxVideosPerRun = 100 };

        public Ambiente()
        {
            VideoSource.Setup(v => v.GetUploadPlaylistIdAsync(It.IsAny<ChannelConfig>(), It.IsAny<CancellationToken>())).ReturnsAsync("playlist-1");
            Progresso.Setup(p => p.GetLastPageTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
            Progresso.Setup(p => p.GetLastProcessedDateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((DateTime?)null);
            Progresso.Setup(p => p.SaveProgressoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            Handler.Setup(h => h.IsRelevant(It.IsAny<Video>())).Returns(true);
            VideoRepo.Setup(r => r.UpsertAsync(It.IsAny<Video>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            Handler.Setup(h => h.JaProcessadoAsync(It.IsAny<Video>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
            Handler.Setup(h => h.HandleAsync(It.IsAny<Video>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            HostEnvironment.Setup(h => h.EnvironmentName).Returns("Testing");
        }

        public ChannelProcessor CriarProcessor() => new(
            VideoSource.Object, Progresso.Object, _ => Handler.Object, VideoRepo.Object, Tracing, SessionContext, ActiveChannel, HostEnvironment.Object, Logger, Microsoft.Extensions.Options.Options.Create(Opcoes));
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
    public async Task RunAsync_VideoMaisAntigoQueADataDeProgressoMasNaoConcluido_EEnfileiradoDeNovo()
    {
        // US #152: lastProcessedDate avançava no ENFILEIRAMENTO; vídeo que falhava depois ficava abaixo da
        // data e nunca mais era reavaliado (22 cultos perdidos). Agora só JaProcessadoAsync decide.
        var ambiente = new Ambiente();
        var agora = DateTime.UtcNow;
        ambiente.Progresso.Setup(p => p.GetLastProcessedDateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(agora); // data de progresso MAIS NOVA que os dois vídeos
        ambiente.Handler.Setup(h => h.JaProcessadoAsync(It.Is<Video>(v => v.Id == "ok"), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        ambiente.VideoSource
            .Setup(v => v.GetPlaylistPageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlaylistPage([CriarVideo("ok", agora.AddDays(-5)), CriarVideo("falhou", agora.AddDays(-6))], NextPageToken: null));

        await ambiente.CriarProcessor().RunAsync([Canal], CancellationToken.None);

        ambiente.Handler.Verify(h => h.HandleAsync(It.Is<Video>(v => v.Id == "falhou"), It.IsAny<CancellationToken>()), Times.Once);
        ambiente.Handler.Verify(h => h.HandleAsync(It.Is<Video>(v => v.Id == "ok"), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_MaxVideosPerRunEstouraNoMeioDaPagina_ParaExatamenteNoTetoENaoAvancaAPagina()
    {
        // Bug real (achado ao vivo, 2026-07-18/19): MaxVideosPerRun é um teto DURO (rate limit
        // real do YouTube/downstream) — processar 1 vídeo a mais só pra "terminar a página" não
        // é aceitável. Mas simplesmente cortar no meio e avançar pra próxima página (o bug
        // ORIGINAL) também não serve — os vídeos que sobrariam ficariam pra trás pra sempre.
        // Correção: página com 3 vídeos relevantes e teto=2 processa EXATAMENTE 2 (nunca 3), e
        // NÃO avança pageToken (fica na mesma página — pageToken salvo continua null, o mesmo
        // usado pra buscar essa página) — a PRÓXIMA execução refaz a mesma página desde o
        // início (ver teste abaixo: JaProcessadoAsync evita reprocessar v1/v2 de graça).
        var ambiente = new Ambiente { Opcoes = new ProcessingOptions { MaxVideosPerRun = 2, DelayBetweenPagesMs = 0, DelayBetweenVideosMs = 0 } };
        var pagina = new PlaylistPage(
            [CriarVideo("v1", DateTime.UtcNow), CriarVideo("v2", DateTime.UtcNow), CriarVideo("v3", DateTime.UtcNow)],
            NextPageToken: "pagina-2");

        ambiente.VideoSource
            .Setup(v => v.GetPlaylistPageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pagina);

        await ambiente.CriarProcessor().RunAsync([Canal], CancellationToken.None);

        ambiente.Handler.Verify(h => h.HandleAsync(It.IsAny<Video>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        ambiente.Handler.Verify(h => h.HandleAsync(It.Is<Video>(vd => vd.Id == "v3"), It.IsAny<CancellationToken>()), Times.Never);
        ambiente.VideoSource.Verify(v => v.GetPlaylistPageAsync(It.IsAny<string>(), It.IsAny<int>(), "pagina-2", It.IsAny<CancellationToken>()), Times.Never);
        // pageToken salvo continua null (a MESMA página, não "pagina-2").
        ambiente.Progresso.Verify(p => p.SaveProgressoAsync(
            It.IsAny<string>(), "playlist-1", null, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_VideoJaProcessado_NaoContaContraOTetoEDeixaEspacoPraNovosNaMesmaPagina()
    {
        // Bug #125/#126: ao retomar a MESMA página do zero (sem posição salva), reavaliar
        // vídeos já concluídos não pode gastar cota do teto — senão, com um teto pequeno e
        // vários vídeos já feitos antes de vídeos novos na mesma página, o run nunca alcançaria
        // os novos, travado pra sempre no mesmo ponto. JaProcessadoAsync=true pula v1 de graça,
        // sobrando cota (teto=1) pra processar v2 de verdade.
        var ambiente = new Ambiente { Opcoes = new ProcessingOptions { MaxVideosPerRun = 1, DelayBetweenPagesMs = 0, DelayBetweenVideosMs = 0 } };
        ambiente.Handler.Setup(h => h.JaProcessadoAsync(It.Is<Video>(vd => vd.Id == "v1"), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var pagina = new PlaylistPage(
            [CriarVideo("v1", DateTime.UtcNow), CriarVideo("v2", DateTime.UtcNow)],
            NextPageToken: null);

        ambiente.VideoSource
            .Setup(v => v.GetPlaylistPageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pagina);

        await ambiente.CriarProcessor().RunAsync([Canal], CancellationToken.None);

        ambiente.Handler.Verify(h => h.HandleAsync(It.Is<Video>(vd => vd.Id == "v1"), It.IsAny<CancellationToken>()), Times.Never);
        ambiente.Handler.Verify(h => h.HandleAsync(It.Is<Video>(vd => vd.Id == "v2"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_MaxVideosPerRunNaoEstouraNaPrimeiraPagina_BuscaSegundaPaginaEParaExatamenteNoTeto()
    {
        var ambiente = new Ambiente { Opcoes = new ProcessingOptions { MaxVideosPerRun = 2, DelayBetweenPagesMs = 0, DelayBetweenVideosMs = 0 } };
        var pagina1 = new PlaylistPage([CriarVideo("v1", DateTime.UtcNow)], NextPageToken: "pagina-2");
        var pagina2 = new PlaylistPage([CriarVideo("v2", DateTime.UtcNow), CriarVideo("v3", DateTime.UtcNow)], NextPageToken: "pagina-3");

        ambiente.VideoSource
            .Setup(v => v.GetPlaylistPageAsync(It.IsAny<string>(), It.IsAny<int>(), (string?)null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pagina1);
        ambiente.VideoSource
            .Setup(v => v.GetPlaylistPageAsync(It.IsAny<string>(), It.IsAny<int>(), "pagina-2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(pagina2);

        await ambiente.CriarProcessor().RunAsync([Canal], CancellationToken.None);

        // Página 1 (1 vídeo, cabe no teto) esgota normalmente e avança pra página 2; na página
        // 2, processedIds já vem de 1 — só v2 cabe no teto (chega a 2), v3 fica pra trás e a
        // página 2 NÃO avança (fica na mesma "pagina-2" pra retomar do zero na próxima execução
        // — sem posição salva, mas JaProcessadoAsync evita reprocessar v2 de graça).
        ambiente.Handler.Verify(h => h.HandleAsync(It.IsAny<Video>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        ambiente.Handler.Verify(h => h.HandleAsync(It.Is<Video>(vd => vd.Id == "v3"), It.IsAny<CancellationToken>()), Times.Never);
        ambiente.VideoSource.Verify(v => v.GetPlaylistPageAsync(It.IsAny<string>(), It.IsAny<int>(), "pagina-3", It.IsAny<CancellationToken>()), Times.Never);
        // Dois saves com o mesmo pageToken "pagina-2": o primeiro ao avançar da página 1 pra 2,
        // o segundo ao parar no meio da página 2 (pageToken não muda, continua "pagina-2").
        ambiente.Progresso.Verify(p => p.SaveProgressoAsync(
            It.IsAny<string>(), "playlist-1", "pagina-2", It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
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
    public async Task RunAsync_ComDoisCanais_ResolveUmIVideoHandlerDiferentePorChannelId()
    {
        // US #105: com N canais numa run só, cada um pode ter seu próprio IVideoHandler
        // (regra de relevância diferente) — o resolver é chamado com o channel_id certo, não
        // reaproveita o handler de um canal pro outro.
        var ambiente = new Ambiente();
        var canalA = new ChannelConfig("UCa", null, "Canal A");
        var canalB = new ChannelConfig("UCb", null, "Canal B");
        var handlerA = new Mock<IVideoHandler>();
        var handlerB = new Mock<IVideoHandler>();
        handlerA.Setup(h => h.IsRelevant(It.IsAny<Video>())).Returns(true);
        handlerB.Setup(h => h.IsRelevant(It.IsAny<Video>())).Returns(true);
        handlerA.Setup(h => h.HandleAsync(It.IsAny<Video>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        handlerB.Setup(h => h.HandleAsync(It.IsAny<Video>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        ambiente.VideoSource
            .Setup(v => v.GetUploadPlaylistIdAsync(It.Is<ChannelConfig>(c => c.ChannelId == "UCa"), It.IsAny<CancellationToken>()))
            .ReturnsAsync("playlist-a");
        ambiente.VideoSource
            .Setup(v => v.GetUploadPlaylistIdAsync(It.Is<ChannelConfig>(c => c.ChannelId == "UCb"), It.IsAny<CancellationToken>()))
            .ReturnsAsync("playlist-b");
        ambiente.VideoSource
            .Setup(v => v.GetPlaylistPageAsync("playlist-a", It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlaylistPage([CriarVideo("va", DateTime.UtcNow)], NextPageToken: null));
        ambiente.VideoSource
            .Setup(v => v.GetPlaylistPageAsync("playlist-b", It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlaylistPage([CriarVideo("vb", DateTime.UtcNow)], NextPageToken: null));

        var processor = new ChannelProcessor(
            ambiente.VideoSource.Object, ambiente.Progresso.Object,
            channelId => channelId switch { "UCa" => handlerA.Object, "UCb" => handlerB.Object, _ => throw new InvalidOperationException(channelId) },
            ambiente.VideoRepo.Object, ambiente.Tracing, ambiente.SessionContext, ambiente.ActiveChannel, ambiente.HostEnvironment.Object, ambiente.Logger,
            Microsoft.Extensions.Options.Options.Create(ambiente.Opcoes));

        await processor.RunAsync([canalA, canalB], CancellationToken.None);

        handlerA.Verify(h => h.HandleAsync(It.Is<Video>(v => v.Id == "va"), It.IsAny<CancellationToken>()), Times.Once);
        handlerA.Verify(h => h.HandleAsync(It.Is<Video>(v => v.Id == "vb"), It.IsAny<CancellationToken>()), Times.Never);
        handlerB.Verify(h => h.HandleAsync(It.Is<Video>(v => v.Id == "vb"), It.IsAny<CancellationToken>()), Times.Once);
        handlerB.Verify(h => h.HandleAsync(It.Is<Video>(v => v.Id == "va"), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(["UCa", "UCb"], ambiente.ActiveChannel.Definidos);
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
    public async Task RunAsync_AbreAHierarquiaCompletaDeTracesAninhados()
    {
        // Board #88: run -> canal -> playlist -> página -> vídeo, todos aninhados (Activity.Current
        // ambient) — não mais só página/vídeo como transações independentes.
        var ambiente = new Ambiente();
        var pagina = new PlaylistPage([CriarVideo("v1", DateTime.UtcNow)], NextPageToken: null);
        ambiente.VideoSource.Setup(v => v.GetPlaylistPageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(pagina);

        await ambiente.CriarProcessor().RunAsync([Canal], CancellationToken.None);

        var nomes = ambiente.Tracing.Traces.Select(t => t.Nome).ToList();
        Assert.Equal(["pipeline.run", "pipeline.canal", "pipeline.playlist", "pipeline.pagina", "pipeline.video"], nomes);
    }

    [Fact]
    public async Task RunAsync_DefineSessaoComUserIgualAoCanalEmTodosOsNiveis()
    {
        // Board #82: durante a pipeline, o User do Helicone deve ser o nome do canal (não um
        // valor genérico) — permite ver custo/uso agrupado por canal na plataforma externa.
        var ambiente = new Ambiente();
        var pagina = new PlaylistPage([CriarVideo("v1", DateTime.UtcNow)], NextPageToken: null);
        ambiente.VideoSource.Setup(v => v.GetPlaylistPageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(pagina);

        await ambiente.CriarProcessor().RunAsync([Canal], CancellationToken.None);

        Assert.All(ambiente.SessionContext.Definidas, sessao => Assert.Equal(Canal.Name, sessao.UserId));
    }

    [Fact]
    public async Task RunAsync_DefineSessionPathComPlaylistPaginaEVideo_MesmoSessionIdEntreVideosDaMesmaRun()
    {
        // Board #82/#88: hierarquia canal->playlist->página->vídeo->etapa no Helicone. O SessionId
        // (que agrupa tudo como UMA árvore, e é o CorrelationId de tudo) é o mesmo do início ao
        // fim da run; o SessionPath diferencia ambiente/canal/playlist/página/vídeo. As etapas
        // (resumo/categorização) acrescentam seu próprio segmento por cima disso (ver
        // ClaudeResumoService/ClaudeCategorizacaoService).
        var ambiente = new Ambiente();
        var pagina = new PlaylistPage([CriarVideo("v1", DateTime.UtcNow), CriarVideo("v2", DateTime.UtcNow)], NextPageToken: null);
        ambiente.VideoSource.Setup(v => v.GetPlaylistPageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(pagina);

        await ambiente.CriarProcessor().RunAsync([Canal], CancellationToken.None);

        var sessoesDeVideo = ambiente.SessionContext.Definidas.Where(s => s.Propriedades!.ContainsKey("VideoId")).ToList();
        Assert.Equal(2, sessoesDeVideo.Count);
        Assert.Equal(sessoesDeVideo[0].SessionId, sessoesDeVideo[1].SessionId); // mesma árvore/sessão pra run inteira
        Assert.Equal($"/pipeline-Testing/{Canal.Name}/playlist-playlist-1/pagina-primeira/v1", sessoesDeVideo[0].SessionPath);
        Assert.Equal($"/pipeline-Testing/{Canal.Name}/playlist-playlist-1/pagina-primeira/v2", sessoesDeVideo[1].SessionPath);

        // Todos os níveis (canal/playlist/página/vídeo) compartilham o mesmo CorrelationId (SessionId)
        Assert.Single(ambiente.SessionContext.Definidas.Select(s => s.SessionId).Distinct());
    }

    [Fact]
    public async Task RunAsync_PropriedadesDaSessaoTrazemEnvironmentCanalPlaylistPaginaEVideo()
    {
        var ambiente = new Ambiente();
        var pagina = new PlaylistPage([CriarVideo("v1", DateTime.UtcNow)], NextPageToken: null);
        ambiente.VideoSource.Setup(v => v.GetPlaylistPageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(pagina);

        await ambiente.CriarProcessor().RunAsync([Canal], CancellationToken.None);

        var propriedades = ambiente.SessionContext.Definidas.Last().Propriedades!;
        Assert.Equal("Testing", propriedades["Environment"]);
        Assert.Equal(Canal.Name, propriedades["Canal"]);
        Assert.Equal("playlist-1", propriedades["Playlist"]);
        Assert.Equal("primeira", propriedades["Pagina"]);
        Assert.Equal("v1", propriedades["VideoId"]);
        Assert.Equal("Vídeo v1", propriedades["VideoTitle"]);
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

    [Fact]
    public async Task RunAsync_GravaTodoVideoVisitadoComORelevante_ESoProcessaOsRelevantes()
    {
        var ambiente = new Ambiente();
        ambiente.VideoSource
            .Setup(v => v.GetPlaylistPageAsync("playlist-1", It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlaylistPage([CriarVideo("relevante", DateTime.UtcNow), CriarVideo("aviso", DateTime.UtcNow)], NextPageToken: null));
        ambiente.Handler.Setup(h => h.IsRelevant(It.Is<Video>(v => v.Id == "aviso"))).Returns(false);

        await ambiente.CriarProcessor().RunAsync([Canal], CancellationToken.None);

        ambiente.VideoRepo.Verify(r => r.UpsertAsync(It.Is<Video>(v => v.Id == "relevante" && v.Relevante == true && v.Status == VideoStatus.Active), It.IsAny<CancellationToken>()), Times.Once);
        ambiente.VideoRepo.Verify(r => r.UpsertAsync(It.Is<Video>(v => v.Id == "aviso" && v.Relevante == false && v.Status == VideoStatus.Active), It.IsAny<CancellationToken>()), Times.Once);
        ambiente.Handler.Verify(h => h.HandleAsync(It.Is<Video>(v => v.Id == "relevante"), It.IsAny<CancellationToken>()), Times.Once);
        ambiente.Handler.Verify(h => h.HandleAsync(It.Is<Video>(v => v.Id == "aviso"), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_VideoRemovidoNoYouTube_GravaSoOStatusENaoProcessa()
    {
        var ambiente = new Ambiente();
        var removido = CriarVideo("v2", DateTime.UtcNow);
        removido.Title = "Deleted video";
        ambiente.VideoSource
            .Setup(v => v.GetPlaylistPageAsync("playlist-1", It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlaylistPage([removido], NextPageToken: null));

        await ambiente.CriarProcessor().RunAsync([Canal], CancellationToken.None);

        ambiente.VideoRepo.Verify(r => r.UpsertAsync(
            It.Is<Video>(v => v.Id == "v2" && v.Status == VideoStatus.Removed && v.Title == "" && v.Relevante == null), It.IsAny<CancellationToken>()), Times.Once);
        ambiente.VideoRepo.Verify(r => r.UpsertAsync(It.IsAny<Video>(), It.IsAny<CancellationToken>()), Times.Once);
        ambiente.Handler.Verify(h => h.HandleAsync(It.IsAny<Video>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_TituloPrivateVideo_NaoGravaNemProcessa()
    {
        var ambiente = new Ambiente();
        var privado = CriarVideo("v2", DateTime.UtcNow);
        privado.Title = "Private video";
        ambiente.VideoSource
            .Setup(v => v.GetPlaylistPageAsync("playlist-1", It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlaylistPage([privado], NextPageToken: null));

        await ambiente.CriarProcessor().RunAsync([Canal], CancellationToken.None);

        ambiente.VideoRepo.Verify(r => r.UpsertAsync(It.IsAny<Video>(), It.IsAny<CancellationToken>()), Times.Never);
        ambiente.Handler.Verify(h => h.HandleAsync(It.IsAny<Video>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_QuandoAGravacaoFalha_ProcessaOsVideosRelevantesMesmoAssim()
    {
        var ambiente = new Ambiente();
        ambiente.VideoSource
            .Setup(v => v.GetPlaylistPageAsync("playlist-1", It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlaylistPage([CriarVideo("v1", DateTime.UtcNow)], NextPageToken: null));
        ambiente.VideoRepo
            .Setup(r => r.UpsertAsync(It.IsAny<Video>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("supabase fora do ar"));

        await ambiente.CriarProcessor().RunAsync([Canal], CancellationToken.None);

        ambiente.Handler.Verify(h => h.HandleAsync(It.Is<Video>(v => v.Id == "v1"), It.IsAny<CancellationToken>()), Times.Once);
    }
}
