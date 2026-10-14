using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Whisper;
using Giom.VideoManager.Infrastructure.YouTube;
using Giom.VideoManager.Infrastructure.YtDlp;
using Giom.VideoManager.Testes.Comuns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.YtDlp;

public class YtDlpArgumentosTests
{
    private const string ProxyComCredencial = "http://usuario:s3nh4-secreta@proxy.exemplo:8080";
    private const string ProviderUrl = "http://127.0.0.1:4416";

    private static YtDlpOptions Opcoes(bool poTokenHabilitado = false, string providerUrl = "", string proxy = "") => new()
    {
        PoToken = new YtDlpPoTokenOptions { Habilitado = poTokenHabilitado, ProviderUrl = providerUrl },
        Proxy = proxy
    };

    // ---------- PO token ----------

    [Fact]
    public void Padrao_TudoDesligado_SemArgumentosComuns_EPluginsDesativados()
    {
        var opcoes = new YtDlpOptions();

        Assert.Empty(YtDlpArgumentos.Comuns(opcoes));
        Assert.Equal("1", YtDlpArgumentos.Ambiente(opcoes)[YtDlpArgumentos.VariavelSemPlugins]);
        Assert.Equal("PoToken=off Proxy=off", YtDlpArgumentos.DescreverParaLog(opcoes));
    }

    [Fact]
    public void PoTokenLigado_PassaBaseUrlDoBgutilHttp_ENaoDesativaPlugins()
    {
        var opcoes = Opcoes(poTokenHabilitado: true, providerUrl: ProviderUrl);

        Assert.Equal(["--extractor-args", "youtubepot-bgutilhttp:base_url=http://127.0.0.1:4416"], YtDlpArgumentos.Comuns(opcoes));
        Assert.Empty(YtDlpArgumentos.Ambiente(opcoes));
        Assert.True(YtDlpArgumentos.PoTokenAtivo(opcoes));
    }

    [Theory]
    [InlineData(true, "")]
    [InlineData(true, "   ")]
    [InlineData(false, ProviderUrl)]
    public void PoTokenIncompleto_FicaDesligado(bool habilitado, string providerUrl)
    {
        var opcoes = Opcoes(poTokenHabilitado: habilitado, providerUrl: providerUrl);

        Assert.False(YtDlpArgumentos.PoTokenAtivo(opcoes));
        Assert.DoesNotContain("--extractor-args", YtDlpArgumentos.Comuns(opcoes));
        Assert.True(YtDlpArgumentos.Ambiente(opcoes).ContainsKey(YtDlpArgumentos.VariavelSemPlugins));
    }

    // ---------- proxy ----------

    [Fact]
    public void ProxyLigado_PassaProxyComoArgumentoUnico()
    {
        var opcoes = Opcoes(proxy: ProxyComCredencial);

        Assert.Equal(["--proxy", ProxyComCredencial], YtDlpArgumentos.Comuns(opcoes));
        Assert.True(YtDlpArgumentos.ProxyAtivo(opcoes));
    }

    [Fact]
    public void ProxyVazio_SemArgumentoProxy()
    {
        Assert.DoesNotContain("--proxy", YtDlpArgumentos.Comuns(Opcoes(proxy: " ")));
    }

    [Fact]
    public void ProxyEPoTokenLigados_AmbosPresentes()
    {
        var args = YtDlpArgumentos.Comuns(Opcoes(poTokenHabilitado: true, providerUrl: ProviderUrl, proxy: ProxyComCredencial));

        Assert.Equal(["--proxy", ProxyComCredencial, "--extractor-args", "youtubepot-bgutilhttp:base_url=http://127.0.0.1:4416"], args);
    }

    [Fact]
    public void DescreverParaLog_NuncaIncluiOValorDoProxy()
    {
        var descricao = YtDlpArgumentos.DescreverParaLog(Opcoes(poTokenHabilitado: true, providerUrl: ProviderUrl, proxy: ProxyComCredencial));

        Assert.Equal("PoToken=on Proxy=on", descricao);
        Assert.DoesNotContain("s3nh4", descricao);
        Assert.DoesNotContain("proxy.exemplo", descricao);
    }

    [Fact]
    public void MascararSegredos_RemoveProxyInteiroECredencial()
    {
        var opcoes = Opcoes(proxy: ProxyComCredencial);

        var inteiro = YtDlpArgumentos.MascararSegredos($"ERROR: Unable to connect to proxy {ProxyComCredencial}", opcoes);
        var soCredencial = YtDlpArgumentos.MascararSegredos("ProxyError: usuario:s3nh4-secreta@proxy.exemplo", opcoes);

        Assert.DoesNotContain("s3nh4", inteiro);
        Assert.DoesNotContain("s3nh4", soCredencial);
        Assert.Contains("***", soCredencial);
    }

    [Fact]
    public void MascararSegredos_SemProxy_DevolveLinhaIntacta()
    {
        const string linha = "ERROR: Sign in to confirm you're not a bot";

        Assert.Equal(linha, YtDlpArgumentos.MascararSegredos(linha, new YtDlpOptions()));
    }

    // ---------- argumentos específicos dos serviços ----------

    [Fact]
    public void YtDlpCaption_MontarArgumentos_CookiesDoCanalEUrlDoVideo_SemProxyNemPoToken()
    {
        var cookies = new Mock<IChannelYtDlpCookies>();
        cookies.Setup(c => c.GetCookiesFilePath("UC-instituto")).Returns("/tmp/cookies instituto.txt");
        var servico = new YtDlpCaptionService(
            Options.Create(new YtDlpCaptionOptions { Languages = ["pt", "en"] }),
            Options.Create(Opcoes(poTokenHabilitado: true, providerUrl: ProviderUrl, proxy: ProxyComCredencial)),
            new FakeLogger<YtDlpCaptionService>(), new FakeTracingService(), cookies.Object);

        var args = servico.MontarArgumentos(new Video { Id = "MQLn0Oxf6aA", ChannelId = "UC-instituto" }, "/tmp/work");

        Assert.Equal(["--cookies", "/tmp/cookies instituto.txt"], args.Take(2));
        Assert.Equal("https://www.youtube.com/watch?v=MQLn0Oxf6aA", args[^1]);
        Assert.Contains("pt,en", args);
        // Comuns entram só no YtDlpProcesso — não duplicar aqui.
        Assert.DoesNotContain("--proxy", args);
        Assert.DoesNotContain("--extractor-args", args);
    }

    [Fact]
    public void Whisper_MontarArgumentosDownload_SemCookies_ExtraiAudioMp3()
    {
        var http = new Mock<IHttpClientFactory>();
        http.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
        var servico = new WhisperTranscricaoService(
            Options.Create(new WhisperOptions()),
            Options.Create(new YtDlpOptions()),
            http.Object, new FakeLogger<WhisperTranscricaoService>(),
            Mock.Of<IContextoTelemetria>(), new NullChannelYtDlpCookies());

        var args = servico.MontarArgumentosDownload("MQLn0Oxf6aA", "UC-sem-cookie", "/tmp/work");

        Assert.DoesNotContain("--cookies", args);
        Assert.Equal(["-x", "--audio-format", "mp3"], args.Take(3));
        Assert.Equal("https://www.youtube.com/watch?v=MQLn0Oxf6aA", args[^1]);
    }

    // ---------- registro / log de boot ----------

    [Fact]
    public void AddYtDlpOpcoes_LeSecaoYtDlp()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["YtDlp:PoToken:Habilitado"] = "true",
                ["YtDlp:PoToken:ProviderUrl"] = ProviderUrl,
                ["YtDlp:Proxy"] = ProxyComCredencial
            })
            .Build();

        var services = new ServiceCollection();
        services.AddYtDlpOpcoes(config);
        services.AddYtDlpOpcoes(config); // idempotente

        var opcoes = services.BuildServiceProvider().GetRequiredService<IOptions<YtDlpOptions>>().Value;

        Assert.True(YtDlpArgumentos.PoTokenAtivo(opcoes));
        Assert.Equal(ProxyComCredencial, opcoes.Proxy);
        Assert.Single(services, d => d.ServiceType == typeof(IConfigureOptions<YtDlpOptions>));
    }

    [Fact]
    public async Task DiagnosticoBoot_LogaSoOnOff_NuncaOProxy_EToleraBinarioAusente()
    {
        var log = new FakeLogger<YtDlpDiagnosticoBoot>();
        var diagnostico = new YtDlpDiagnosticoBoot(
            Options.Create(Opcoes(poTokenHabilitado: true, providerUrl: ProviderUrl, proxy: ProxyComCredencial)), log);

        await diagnostico.StartAsync(CancellationToken.None);

        Assert.Contains(log.Entradas, e => e.Mensagem.Contains("PoToken=on Proxy=on"));
        Assert.Contains(log.Entradas, e => e.Mensagem.StartsWith("yt-dlp"));
        Assert.Contains(log.Entradas, e => e.Mensagem.StartsWith("deno"));
        Assert.DoesNotContain(log.Entradas, e => e.Mensagem.Contains("s3nh4") || e.Mensagem.Contains("proxy.exemplo"));
        Assert.DoesNotContain(log.Entradas, e => e.Nivel >= LogLevel.Error);
    }
}
