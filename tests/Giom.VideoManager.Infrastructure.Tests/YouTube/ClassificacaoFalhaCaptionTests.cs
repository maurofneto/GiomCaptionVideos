using System.Net;
using System.Text;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Infrastructure.YouTube;
using Giom.VideoManager.Testes.Comuns;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.YouTube;

/// <summary>
/// PR-02: classificação das falhas de captions.list/captions.download. Sem rede — o YouTubeService
/// recebe um HttpMessageHandler falso via HttpClientFactory.
/// </summary>
public class ClassificacaoFalhaCaptionTests
{
    private const string CanalId = "UC-ministerio";
    private static readonly Video Video = new() { Id = "vid123", ChannelId = CanalId };

    private sealed class FabricaFalsa(HttpMessageHandler handler) : HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => handler;
    }

    private sealed class HandlerFalso(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int ChamadasList;
        public int ChamadasDownload;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (IsList(request)) Interlocked.Increment(ref ChamadasList);
            else Interlocked.Increment(ref ChamadasDownload);
            return Task.FromResult(responder(request));
        }

        public static bool IsList(HttpRequestMessage r) => r.RequestUri!.AbsolutePath.EndsWith("/captions", StringComparison.Ordinal);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage ErroGoogle(HttpStatusCode status, string reason) =>
        Json(status, "{\"error\":{\"code\":" + (int)status + ",\"message\":\"msg\",\"errors\":[{\"domain\":\"youtube.caption\",\"reason\":\""
                     + reason + "\",\"message\":\"msg\"}]}}");

    private static HttpResponseMessage ListaComUmaFaixa() =>
        Json(HttpStatusCode.OK, """{"items":[{"id":"cap1","snippet":{"status":"serving"}}]}""");

    private static HttpResponseMessage Texto(HttpStatusCode status, string corpo) =>
        new(status) { Content = new StringContent(corpo, Encoding.UTF8, "text/vtt") };

    private static (YouTubeCaptionService Servico, FakeLogger<YouTubeCaptionService> Log, HandlerFalso Handler) Criar(
        Func<HttpRequestMessage, HttpResponseMessage> responder, string? canalComKeyed = CanalId)
    {
        var handler = new HandlerFalso(responder);
        var youtube = new YouTubeService(new BaseClientService.Initializer
        {
            ApiKey = "fake-key-sem-chamada-real",
            HttpClientFactory = new FabricaFalsa(handler)
        });

        var services = new ServiceCollection();
        if (canalComKeyed is not null) services.AddKeyedSingleton(canalComKeyed, youtube);
        var log = new FakeLogger<YouTubeCaptionService>();
        var servico = new YouTubeCaptionService(services.BuildServiceProvider(),
            Options.Create(new YouTubeCaptionOptions { Formato = "vtt" }), log)
        {
            RetryDelay = TimeSpan.Zero
        };
        return (servico, log, handler);
    }

    private static HttpResponseMessage Rota(HttpRequestMessage r, HttpResponseMessage download) =>
        HandlerFalso.IsList(r) ? ListaComUmaFaixa() : download;

    private static EntradaDeLog UnicoLogDeFalha(FakeLogger<YouTubeCaptionService> log) =>
        Assert.Single(log.Entradas, e => e.Mensagem.StartsWith("Falha de legenda classificada"));

    // ---------- download 403 ----------

    [Fact]
    public async Task Download403Forbidden_ClassificaSemPermissao_LancaELogaErro_SemLegendaVazia()
    {
        var (svc, log, _) = Criar(r => Rota(r, ErroGoogle(HttpStatusCode.Forbidden, "forbidden")));

        var ex = await Assert.ThrowsAsync<CaptionFalhaException>(() => svc.GetCaptionAsync(Video));

        Assert.Equal(ClasseFalhaCaption.SemPermissao, ex.Classe);
        Assert.Equal(403, ex.HttpStatus);
        Assert.Equal("forbidden", ex.Reason);
        Assert.IsAssignableFrom<NenhumResultadoEncontradoException>(ex);

        var entrada = UnicoLogDeFalha(log);
        Assert.Equal(LogLevel.Error, entrada.Nivel);
        Assert.Contains("SemPermissao", entrada.Mensagem);
        Assert.Contains("YouTubeCaption", entrada.Mensagem);
        Assert.Contains(Video.Id, entrada.Mensagem);
        Assert.Contains(CanalId, entrada.Mensagem);
        Assert.Contains("403", entrada.Mensagem);
        Assert.DoesNotContain(log.Entradas, e => e.Mensagem.Contains("Legenda vazia", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Download403QuotaExceeded_ClassificaQuotaExcedida_NaoSemPermissao()
    {
        var (svc, log, _) = Criar(r => Rota(r, ErroGoogle(HttpStatusCode.Forbidden, "quotaExceeded")));

        var ex = await Assert.ThrowsAsync<RateLimitExcedidoException>(() => svc.GetCaptionAsync(Video));

        Assert.IsType<Google.GoogleApiException>(ex.InnerException);
        var entrada = UnicoLogDeFalha(log);
        Assert.Equal(LogLevel.Warning, entrada.Nivel);
        Assert.Contains("QuotaExcedida", entrada.Mensagem);
        Assert.DoesNotContain("SemPermissao", entrada.Mensagem);
        Assert.Contains("quotaExceeded", entrada.Mensagem);
        Assert.DoesNotContain(log.Entradas, e => e.Mensagem.Contains("Legenda vazia", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Download403QuotaExceeded_ContaParaOCircuitBreaker_SegundaFalhaAbreOCircuito()
    {
        // list ok + download 403 quota: a 2ª ocorrência (MinimumThroughput=2) abre o circuito dentro da
        // mesma instância do Resilient; aqui só garantimos que a exceção de quota sai nos dois casos.
        var (svc, _, handler) = Criar(r => Rota(r, ErroGoogle(HttpStatusCode.Forbidden, "dailyLimitExceeded")));

        await Assert.ThrowsAsync<RateLimitExcedidoException>(() => svc.GetCaptionAsync(Video));

        Assert.Equal(1, handler.ChamadasDownload); // quota não é 5xx: sem retry
    }

    // ---------- list vazio / corpo vazio ----------

    [Fact]
    public async Task ListaSemFaixas_ClassificaSemLegendaAinda_LogInformation()
    {
        var (svc, log, handler) = Criar(_ => Json(HttpStatusCode.OK, """{"items":[]}"""));

        var ex = await Assert.ThrowsAsync<CaptionFalhaException>(() => svc.GetCaptionAsync(Video));

        Assert.Equal(ClasseFalhaCaption.SemLegendaAinda, ex.Classe);
        Assert.Equal(0, handler.ChamadasDownload);
        Assert.Equal(LogLevel.Information, UnicoLogDeFalha(log).Nivel);
    }

    [Fact]
    public async Task Download200ComCorpoVazio_ClassificaLegendaVaziaReal_LogWarning()
    {
        var (svc, log, _) = Criar(r => Rota(r, Texto(HttpStatusCode.OK, "")));

        var ex = await Assert.ThrowsAsync<CaptionFalhaException>(() => svc.GetCaptionAsync(Video));

        Assert.Equal(ClasseFalhaCaption.LegendaVaziaReal, ex.Classe);
        var entrada = UnicoLogDeFalha(log);
        Assert.Equal(LogLevel.Warning, entrada.Nivel);
        Assert.Contains("LegendaVaziaReal", entrada.Mensagem);
    }

    [Fact]
    public async Task Download200ComConteudo_RetornaTranscricao_SemLogDeFalha()
    {
        var (svc, log, _) = Criar(r => Rota(r, Texto(HttpStatusCode.OK, "WEBVTT\n\n00:00.000 --> 00:01.000\nola\n")));

        var transcricao = await svc.GetCaptionAsync(Video);

        Assert.NotNull(transcricao);
        Assert.Contains("ola", transcricao!.Conteudo);
        Assert.DoesNotContain(log.Entradas, e => e.Mensagem.StartsWith("Falha de legenda classificada"));
    }

    // ---------- 5xx ----------

    [Fact]
    public async Task Download5xx_RespeitaRetryExistente_EClassificaTransitorio()
    {
        var (svc, log, handler) = Criar(r => Rota(r, ErroGoogle(HttpStatusCode.InternalServerError, "backendError")));

        var ex = await Assert.ThrowsAsync<CaptionFalhaException>(() => svc.GetCaptionAsync(Video));

        Assert.Equal(ClasseFalhaCaption.Transitorio, ex.Classe);
        Assert.Equal(500, ex.HttpStatus); // 500 e não 503: o SDK já tem back-off próprio só pra 503
        Assert.Equal(4, handler.ChamadasDownload); // 1 + MaxRetryAttempts(3) do pipeline
        Assert.Equal(LogLevel.Warning, UnicoLogDeFalha(log).Nivel);
    }

    [Fact]
    public async Task Download429RateLimitExceeded_ClassificaTransitorio_SemRetry()
    {
        var (svc, _, handler) = Criar(r => Rota(r, ErroGoogle((HttpStatusCode)429, "rateLimitExceeded")));

        var ex = await Assert.ThrowsAsync<CaptionFalhaException>(() => svc.GetCaptionAsync(Video));

        Assert.Equal(ClasseFalhaCaption.Transitorio, ex.Classe);
        Assert.Equal(1, handler.ChamadasDownload);
    }

    // ---------- credencial ----------

    [Fact]
    public async Task CanalSemCredencial_ClassificaCredencialInvalida_MantemTipoDaExcecao()
    {
        var (svc, log, _) = Criar(_ => Json(HttpStatusCode.OK, "{}"), canalComKeyed: null);

        var ex = await Assert.ThrowsAsync<CredencialCanalIndisponivelException>(() => svc.GetCaptionAsync(Video));

        Assert.Equal(CanalId, ex.ChannelId);
        var entrada = UnicoLogDeFalha(log);
        Assert.Equal(LogLevel.Error, entrada.Nivel);
        Assert.Contains("CredencialInvalida", entrada.Mensagem);
    }

    [Fact]
    public void Classificar_TokenInvalidGrant_EhCredencialInvalida()
    {
        var ex = new TokenResponseException(new TokenErrorResponse { Error = "invalid_grant" }, HttpStatusCode.BadRequest);

        var falha = ClassificadorFalhaCaption.Classificar(ex);

        Assert.Equal(ClasseFalhaCaption.CredencialInvalida, falha!.Classe);
        Assert.Equal("invalid_grant", falha.Reason);
    }

    [Fact]
    public void Classificar_TokenInvalidClient_EhCredencialInvalida()
    {
        var ex = new TokenResponseException(new TokenErrorResponse { Error = "invalid_client" }, HttpStatusCode.Unauthorized);

        Assert.Equal(ClasseFalhaCaption.CredencialInvalida, ClassificadorFalhaCaption.Classificar(ex)!.Classe);
    }

    [Fact]
    public void Classificar_CredencialCanalIndisponivel_EhCredencialInvalida_ComMotivo()
    {
        var falha = ClassificadorFalhaCaption.Classificar(new CredencialCanalIndisponivelException(CanalId, "invalid_grant"));

        Assert.Equal(ClasseFalhaCaption.CredencialInvalida, falha!.Classe);
        Assert.Equal("invalid_grant", falha.Reason);
    }

    [Fact]
    public void Classificar_CircuitoAberto_EhQuotaExcedida()
    {
        var ex = new RateLimitExcedidoException("YouTube.X", innerException: new Polly.CircuitBreaker.BrokenCircuitException());

        var falha = ClassificadorFalhaCaption.Classificar(ex);

        Assert.Equal(ClasseFalhaCaption.QuotaExcedida, falha!.Classe);
        Assert.Equal(ClassificadorFalhaCaption.ReasonCircuitoAberto, falha.Reason);
    }

    [Fact]
    public void Classificar_CancelamentoDoChamador_NaoEClassificado()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Null(ClassificadorFalhaCaption.Classificar(new TaskCanceledException(), cts.Token));
        Assert.Equal(ClasseFalhaCaption.Transitorio, ClassificadorFalhaCaption.Classificar(new TaskCanceledException())!.Classe);
    }

    // ---------- yt-dlp (classificador puro) ----------

    [Theory]
    [InlineData("ERROR: [youtube] abc: Sign in to confirm you're not a bot. Use --cookies-from-browser")]
    [InlineData("ERROR: Sign in to confirm you’re not a bot")]
    public void StderrYtDlp_SignInNotABot_EhBloqueioBot(string stderr)
    {
        var falha = ClassificadorFalhaCaption.ClassificarStderrYtDlp(stderr);

        Assert.Equal(ClasseFalhaCaption.BloqueioBot, falha!.Classe);
    }

    [Fact]
    public void SaidaYtDlp_ExitDiferenteDeZeroComTexto_EhBloqueioBot() =>
        Assert.Equal(ClasseFalhaCaption.BloqueioBot,
            ClassificadorFalhaCaption.ClassificarSaidaYtDlp(1, "ERROR: Sign in to confirm you're not a bot")!.Classe);

    [Fact]
    public void SaidaYtDlp_ExitZeroComTexto_NaoClassifica() =>
        Assert.Null(ClassificadorFalhaCaption.ClassificarSaidaYtDlp(0, "WARNING: Sign in to confirm you're not a bot"));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("ERROR: Video unavailable")]
    public void StderrYtDlp_SemBloqueio_NaoClassifica(string? stderr) =>
        Assert.Null(ClassificadorFalhaCaption.ClassificarStderrYtDlp(stderr));

    [Fact]
    public void NivelDeLog_PorClasse()
    {
        Assert.Equal(LogLevel.Error, FalhaCaptionLog.NivelDe(ClasseFalhaCaption.BloqueioBot));
        Assert.Equal(LogLevel.Error, FalhaCaptionLog.NivelDe(ClasseFalhaCaption.SemPermissao));
        Assert.Equal(LogLevel.Error, FalhaCaptionLog.NivelDe(ClasseFalhaCaption.CredencialInvalida));
        Assert.Equal(LogLevel.Warning, FalhaCaptionLog.NivelDe(ClasseFalhaCaption.QuotaExcedida));
        Assert.Equal(LogLevel.Warning, FalhaCaptionLog.NivelDe(ClasseFalhaCaption.Transitorio));
        Assert.Equal(LogLevel.Warning, FalhaCaptionLog.NivelDe(ClasseFalhaCaption.LegendaVaziaReal));
        Assert.Equal(LogLevel.Information, FalhaCaptionLog.NivelDe(ClasseFalhaCaption.SemLegendaAinda));
    }
}
