using System.Net;
using System.Text;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Giom.VideoManager.Infrastructure.GrokVoice.Tests;

public class GrokVoiceCaptionServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "giom_grokvoice_tests", Guid.NewGuid().ToString("N"));

    public GrokVoiceCaptionServiceTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private static Video NovoVideo() => new() { Id = "abc123DEF45", ChannelId = "UCcanalTeste", Title = "Aula" };

    private (GrokVoiceCaptionService Servico, StubHandler Handler, AudioFalso Audio, TelemetriaFalsa Telemetria) Criar(
        HttpResponseMessage resposta, string apiKey = "xai-chave-de-teste")
    {
        var handler = new StubHandler(resposta);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.x.ai/") };
        var audio = new AudioFalso();
        var telemetria = new TelemetriaFalsa();
        var options = Options.Create(new GrokVoiceOptions { ApiKey = apiKey, TempDirectory = _tempDir });
        var servico = new GrokVoiceCaptionService(options, new FabricaHttp(http), audio, telemetria, NullLogger<GrokVoiceCaptionService>.Instance);
        return (servico, handler, audio, telemetria);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Sucesso_DevolveTranscricaoComOrigemGrokVoice()
    {
        var (servico, handler, audio, telemetria) = Criar(Json(HttpStatusCode.OK,
            """{"text":"Boa noite, igreja. Vamos orar.","language":"pt","duration":3600.0,"words":[{"text":"Boa","start":0.0,"end":0.4}]}"""));

        var resultado = await servico.GetCaptionAsync(NovoVideo());

        Assert.NotNull(resultado);
        Assert.Equal("abc123DEF45", resultado!.VideoId);
        Assert.Equal("GrokVoice", resultado.Origem);
        Assert.Equal("txt", resultado.Formato);
        Assert.Equal("Boa noite, igreja. Vamos orar.", resultado.Conteudo);
        Assert.Equal(resultado.Conteudo, resultado.ConteudoLimpo);
        Assert.Equal(("abc123DEF45", "UCcanalTeste"), audio.UltimaChamada);
        Assert.Equal("https://api.x.ai/v1/stt", handler.UltimaRequisicao!.RequestUri!.ToString());
        Assert.Equal("Bearer xai-chave-de-teste", handler.UltimaRequisicao.Headers.Authorization!.ToString());
        Assert.Equal("xAI", telemetria.Provedor);
        Assert.Equal(0.10m, Math.Round(telemetria.CustoUsd!.Value, 2)); // 1 hora de áudio a US$0,10/h
    }

    [Fact]
    public async Task EnviaModeloIdiomaEFormatoNoMultipart()
    {
        var (servico, handler, _, _) = Criar(Json(HttpStatusCode.OK, """{"text":"Olá.","duration":1.0}"""));

        await servico.GetCaptionAsync(NovoVideo());

        var corpo = handler.UltimoCorpo!;
        Assert.Contains("grok-voice-transcribe-2.0", corpo);
        Assert.Contains("name=language", corpo);
        Assert.Contains("name=format", corpo);
        Assert.Contains("name=file", corpo);
    }

    [Fact]
    public async Task MusicaERepeticao_SaoLimpasAntesDeGravar()
    {
        var (servico, _, _, _) = Criar(Json(HttpStatusCode.OK,
            """{"text":"Música. Boa noite. Boa noite. Boa noite. Boa noite.","duration":10.0}"""));

        var resultado = await servico.GetCaptionAsync(NovoVideo());

        Assert.Equal("Boa noite. Boa noite.", resultado!.Conteudo);
    }

    [Fact]
    public async Task TextoVazio_LancaNenhumResultadoEncontrado()
    {
        var (servico, _, _, _) = Criar(Json(HttpStatusCode.OK, """{"text":"","duration":5.0}"""));

        await Assert.ThrowsAsync<NenhumResultadoEncontradoException>(() => servico.GetCaptionAsync(NovoVideo()));
    }

    [Fact]
    public async Task SoMusica_LancaNenhumResultadoEncontrado()
    {
        var (servico, _, _, _) = Criar(Json(HttpStatusCode.OK, """{"text":"Música.","duration":5.0}"""));

        await Assert.ThrowsAsync<NenhumResultadoEncontradoException>(() => servico.GetCaptionAsync(NovoVideo()));
    }

    [Fact]
    public async Task Http429_LancaRateLimitExcedido()
    {
        var (servico, _, _, _) = Criar(Json((HttpStatusCode)429, """{"error":"rate limit"}"""));

        await Assert.ThrowsAsync<RateLimitExcedidoException>(() => servico.GetCaptionAsync(NovoVideo()));
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task OutrosErrosHttp_LancamHttpRequestExceptionComStatus(HttpStatusCode status)
    {
        var (servico, _, _, _) = Criar(Json(status, """{"error":"falha"}"""));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => servico.GetCaptionAsync(NovoVideo()));

        Assert.Equal(status, ex.StatusCode);
    }

    [Fact]
    public async Task SemApiKey_FalhaSemBaixarAudioNemChamarApi()
    {
        var (servico, handler, audio, _) = Criar(Json(HttpStatusCode.OK, """{"text":"x","duration":1}"""), apiKey: "");

        await Assert.ThrowsAsync<InvalidOperationException>(() => servico.GetCaptionAsync(NovoVideo()));

        Assert.Null(audio.UltimaChamada);
        Assert.Null(handler.UltimaRequisicao);
    }

    [Fact]
    public async Task FalhaNoDownload_PropagaEApagaDiretorioDeTrabalho()
    {
        var (servico, handler, audio, _) = Criar(Json(HttpStatusCode.OK, """{"text":"x","duration":1}"""));
        audio.Falhar = true;

        await Assert.ThrowsAsync<NenhumResultadoEncontradoException>(() => servico.GetCaptionAsync(NovoVideo()));

        Assert.Null(handler.UltimaRequisicao);
        Assert.False(Directory.Exists(Path.Combine(_tempDir, "giom_grokvoice", "abc123DEF45")));
    }

    [Fact]
    public async Task Sucesso_ApagaDiretorioDeTrabalho()
    {
        var (servico, _, _, _) = Criar(Json(HttpStatusCode.OK, """{"text":"Olá, igreja.","duration":2.0}"""));

        await servico.GetCaptionAsync(NovoVideo());

        Assert.False(Directory.Exists(Path.Combine(_tempDir, "giom_grokvoice", "abc123DEF45")));
    }

    // ---- dublês ----

    private sealed class StubHandler(HttpResponseMessage resposta) : HttpMessageHandler
    {
        public HttpRequestMessage? UltimaRequisicao { get; private set; }
        public string? UltimoCorpo { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            UltimaRequisicao = request;
            UltimoCorpo = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return resposta;
        }
    }

    private sealed class FabricaHttp(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class AudioFalso : IAudioDownloader
    {
        public (string VideoId, string? ChannelId)? UltimaChamada { get; private set; }
        public bool Falhar { get; set; }

        public Task<string> BaixarAudioAsync(string videoId, string? channelId, string workDir, CancellationToken ct = default)
        {
            UltimaChamada = (videoId, channelId);
            if (Falhar) throw new NenhumResultadoEncontradoException("YtDlpAudio", "bloqueado");
            var path = Path.Combine(workDir, $"{videoId}.mp3");
            File.WriteAllBytes(path, [0x49, 0x44, 0x33]);
            return Task.FromResult(path);
        }
    }

    private sealed class TelemetriaFalsa : IContextoTelemetria
    {
        public string? Provedor { get; private set; }
        public decimal? CustoUsd { get; private set; }

        public void Registrar(string? provedor = null, string? modelo = null, int? tokensEntrada = null, int? tokensSaida = null,
            decimal? custoEstimadoUsd = null, object? detalhes = null, string? requestIdExterno = null)
        {
            Provedor = provedor;
            CustoUsd = custoEstimadoUsd;
        }

        public MetadadosTelemetria IniciarEscopo(bool reaproveitarEscopoExistente = false) => throw new NotSupportedException();
        public MetadadosTelemetria? Atual => null;
    }
}
