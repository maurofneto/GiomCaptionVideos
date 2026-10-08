using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Infrastructure.YouTube;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Http;
using Google.Apis.YouTube.v3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.YouTube;

public class CredenciaisPorCanalTests
{
    private const string SegredoInstituto = "segredo-instituto-xyz";
    private const string TokenInstituto = "token-instituto-xyz";

    private sealed class CredencialFalsa : IConfigurableHttpClientInitializer
    {
        public void Initialize(ConfigurableHttpClient httpClient) { }
    }

    private sealed class LoggerCapturador : ILogger
    {
        public List<string> Mensagens { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Mensagens.Add(formatter(state, exception) + (exception is null ? "" : " | " + exception));
    }

    private static CanalIngestaoConfig Canal(string channelId, string secretsKey) =>
        new(channelId, null, secretsKey, true, true, RelevanciaTipo.LiveUnlisted, [], 10, 0, 0, secretsKey);

    private static IConfiguration Config(Dictionary<string, string?> valores) =>
        new ConfigurationBuilder().AddInMemoryCollection(valores).Build();

    private sealed class Fabrica
    {
        public List<GoogleCredentialOptions> Chamadas { get; } = [];
        public Func<GoogleCredentialOptions, Exception?> Falha { get; init; } = _ => null;

        public Task<IConfigurableHttpClientInitializer> Criar(GoogleCredentialOptions options, CancellationToken ct)
        {
            Chamadas.Add(options);
            var falha = Falha(options);
            return falha is null ? Task.FromResult<IConfigurableHttpClientInitializer>(new CredencialFalsa()) : Task.FromException<IConfigurableHttpClientInitializer>(falha);
        }
    }

    [Fact]
    public void RefreshTokenAusenteOuVazio_PulaCanalSemFabricaENaoAfetaOutro()
    {
        var config = Config(new()
        {
            ["Google:ClientId"] = "111-compartilhado.apps.googleusercontent.com",
            ["instituto:Google:ClientSecret"] = SegredoInstituto,
            ["instituto:Google:RefreshToken"] = "",
            ["ministerio:Google:ClientSecret"] = "segredo-ministerio",
            ["ministerio:Google:RefreshToken"] = "token-ministerio",
            ["sem-token:Google:ClientSecret"] = "x",
        });
        var fabrica = new Fabrica();
        var services = new ServiceCollection();

        var validos = services.AddYouTubeCredenciaisPorCanal(
            config, [Canal("UC-i", "instituto"), Canal("UC-m", "ministerio"), Canal("UC-s", "sem-token")],
            new LoggerCapturador(), fabrica.Criar);

        Assert.Equal(["UC-m"], validos.Select(c => c.ChannelId));
        Assert.Single(fabrica.Chamadas);
        Assert.Equal("segredo-ministerio", fabrica.Chamadas[0].ClientSecret);

        var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetKeyedService<YouTubeService>("UC-m"));
        Assert.Null(sp.GetKeyedService<YouTubeService>("UC-i"));
        var falhas = sp.GetRequiredService<FalhasCredencialPorCanal>();
        Assert.True(falhas.TentarObterMotivo("UC-i", out var motivoI));
        Assert.Contains("RefreshToken ausente", motivoI);
        Assert.True(falhas.TentarObterMotivo("UC-s", out var motivoS));
        Assert.Contains("RefreshToken ausente", motivoS);
        Assert.False(falhas.TentarObterMotivo("UC-m", out _));
    }

    [Fact]
    public void ClientIdPorCanalTemPrecedenciaSobreCompartilhado()
    {
        var config = Config(new()
        {
            ["Google:ClientId"] = "111-compartilhado.apps.googleusercontent.com",
            ["instituto:Google:ClientId"] = "222-proprio.apps.googleusercontent.com",
            ["instituto:Google:ClientSecret"] = "s1",
            ["instituto:Google:RefreshToken"] = "t1",
            ["ministerio:Google:ClientSecret"] = "s2",
            ["ministerio:Google:RefreshToken"] = "t2",
        });
        var fabrica = new Fabrica();
        var logger = new LoggerCapturador();

        new ServiceCollection().AddYouTubeCredenciaisPorCanal(
            config, [Canal("UC-i", "instituto"), Canal("UC-m", "ministerio")], logger, fabrica.Criar);

        Assert.Equal("222-proprio.apps.googleusercontent.com", fabrica.Chamadas[0].ClientId);
        Assert.Equal("111-compartilhado.apps.googleusercontent.com", fabrica.Chamadas[1].ClientId);
        Assert.Contains(logger.Mensagens, m => m.Contains("por-canal") && m.Contains("projeto=222")
            && m.Contains("clientId=222-proprio.apps.googleusercontent.com"));
        Assert.Contains(logger.Mensagens, m => m.Contains("compartilhado") && m.Contains("projeto=111")
            && m.Contains("clientId=111-compartilhado.apps.googleusercontent.com"));
    }

    [Fact]
    public void LogDeBoot_TrazClientIdInteiroNasDuasLinhas_SemClientSecretNemRefreshToken()
    {
        const string clientId = "1069116336751-clientweb.apps.googleusercontent.com";
        var config = Config(new()
        {
            ["Google:ClientId"] = clientId,
            ["instituto:Google:ClientSecret"] = SegredoInstituto,
            ["instituto:Google:RefreshToken"] = TokenInstituto,
        });
        var logger = new LoggerCapturador();

        new ServiceCollection().AddYouTubeCredenciaisPorCanal(config, [Canal("UC-i", "instituto")], logger, new Fabrica().Criar);

        var origem = Assert.Single(logger.Mensagens, m => m.Contains("ClientId origem="));
        var ok = Assert.Single(logger.Mensagens, m => m.StartsWith("Credencial Google OK"));
        foreach (var linha in new[] { origem, ok })
        {
            Assert.Contains($"clientId={clientId}", linha);
            Assert.Contains("projeto=1069116336751", linha);
            Assert.DoesNotContain(SegredoInstituto, linha);
            Assert.DoesNotContain(TokenInstituto, linha);
        }
    }

    [Theory]
    [InlineData("invalid_grant")]
    [InlineData("unauthorized_client")]
    public void RefreshRecusadoPeloGoogle_MotivoTrazCodigoEDicaDeOutroClient_SemDescricaoCrua(string codigo)
    {
        const string descricaoCrua = "DESCRICAO-CRUA-DO-GOOGLE-123";
        var config = Config(new()
        {
            ["Google:ClientId"] = "1069116336751-clientweb.apps.googleusercontent.com",
            ["instituto:Google:ClientSecret"] = SegredoInstituto,
            ["instituto:Google:RefreshToken"] = TokenInstituto,
        });
        var fabrica = new Fabrica
        {
            Falha = _ => new TokenResponseException(new TokenErrorResponse { Error = codigo, ErrorDescription = descricaoCrua })
        };
        var logger = new LoggerCapturador();
        var services = new ServiceCollection();

        var validos = services.AddYouTubeCredenciaisPorCanal(config, [Canal("UC-i", "instituto")], logger, fabrica.Criar);

        Assert.Empty(validos);
        Assert.True(services.BuildServiceProvider().GetRequiredService<FalhasCredencialPorCanal>().TentarObterMotivo("UC-i", out var motivo));
        Assert.Contains(codigo, motivo);
        Assert.Contains("revogado/expirado", motivo);
        Assert.Contains("outro client OAuth", motivo);
        Assert.Contains("ClientId diferente", motivo);

        Assert.DoesNotContain(descricaoCrua, motivo);
        Assert.DoesNotContain(logger.Mensagens, m => m.Contains(descricaoCrua) || m.Contains(SegredoInstituto) || m.Contains(TokenInstituto));
        Assert.Contains(logger.Mensagens, m => m.Contains("Falha") && m.Contains("outro client OAuth"));
    }

    [Fact]
    public void SemNenhumClientId_CanalFalhaComMensagemClara()
    {
        var config = Config(new()
        {
            ["instituto:Google:ClientSecret"] = "s1",
            ["instituto:Google:RefreshToken"] = "t1",
        });
        var fabrica = new Fabrica();
        var services = new ServiceCollection();

        var validos = services.AddYouTubeCredenciaisPorCanal(config, [Canal("UC-i", "instituto")], new LoggerCapturador(), fabrica.Criar);

        Assert.Empty(validos);
        Assert.Empty(fabrica.Chamadas);
        Assert.True(services.BuildServiceProvider().GetRequiredService<FalhasCredencialPorCanal>().TentarObterMotivo("UC-i", out var motivo));
        Assert.Contains("ClientId ausente", motivo);
    }

    [Fact]
    public void ChavesLegadasSemPrefixoNaoSaoUsadas_ComAvisoSemValor()
    {
        var config = Config(new()
        {
            ["Google:ClientId"] = "111-compartilhado.apps.googleusercontent.com",
            ["Google:ClientSecret"] = "segredo-legado",
            ["Google:RefreshToken"] = "token-legado",
        });
        var fabrica = new Fabrica();
        var logger = new LoggerCapturador();
        var services = new ServiceCollection();

        var validos = services.AddYouTubeCredenciaisPorCanal(config, [Canal("UC-i", "instituto")], logger, fabrica.Criar);

        Assert.Empty(validos);
        Assert.Empty(fabrica.Chamadas);
        Assert.True(services.BuildServiceProvider().GetRequiredService<FalhasCredencialPorCanal>().TentarObterMotivo("UC-i", out var motivo));
        Assert.Contains("ClientSecret ausente", motivo);
        Assert.Contains(logger.Mensagens, m => m.Contains("legadas") && m.Contains("IGNORADAS"));
        Assert.DoesNotContain(logger.Mensagens, m => m.Contains("segredo-legado") || m.Contains("token-legado"));
    }

    [Fact]
    public void FalhaDeRefreshDeUmCanal_GuardaMotivoSemSegredoENaoDerrubaOutro()
    {
        var config = Config(new()
        {
            ["Google:ClientId"] = "111-compartilhado.apps.googleusercontent.com",
            ["instituto:Google:ClientSecret"] = SegredoInstituto,
            ["instituto:Google:RefreshToken"] = TokenInstituto,
            ["ministerio:Google:ClientSecret"] = "s2",
            ["ministerio:Google:RefreshToken"] = "t2",
        });
        var fabrica = new Fabrica
        {
            Falha = o => o.RefreshToken == TokenInstituto
                ? new HttpRequestException($"falha com {o.ClientSecret} e {o.RefreshToken}")
                : null
        };
        var logger = new LoggerCapturador();
        var services = new ServiceCollection();

        var validos = services.AddYouTubeCredenciaisPorCanal(
            config, [Canal("UC-i", "instituto"), Canal("UC-m", "ministerio")], logger, fabrica.Criar);

        Assert.Equal(["UC-m"], validos.Select(c => c.ChannelId));
        var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetKeyedService<YouTubeService>("UC-m"));
        Assert.True(sp.GetRequiredService<FalhasCredencialPorCanal>().TentarObterMotivo("UC-i", out var motivo));
        Assert.Contains(nameof(HttpRequestException), motivo);
        Assert.DoesNotContain(SegredoInstituto, motivo);
        Assert.DoesNotContain(TokenInstituto, motivo);
        Assert.DoesNotContain(logger.Mensagens, m => m.Contains(SegredoInstituto) || m.Contains(TokenInstituto));
        Assert.Contains(logger.Mensagens, m => m.Contains("secrets_key=instituto") && m.Contains("Falha"));
    }

    [Fact]
    public void LogDeSucesso_TrazSecretsKeyEHashDistintoPorCanal_SemSegredo()
    {
        var config = Config(new()
        {
            ["Google:ClientId"] = "111-compartilhado.apps.googleusercontent.com",
            ["instituto:Google:ClientSecret"] = SegredoInstituto,
            ["instituto:Google:RefreshToken"] = TokenInstituto,
            ["ministerio:Google:ClientSecret"] = "segredo-ministerio-abc",
            ["ministerio:Google:RefreshToken"] = "token-ministerio-abc",
        });
        var logger = new LoggerCapturador();

        new ServiceCollection().AddYouTubeCredenciaisPorCanal(
            config, [Canal("UC-i", "instituto"), Canal("UC-m", "ministerio")], logger, new Fabrica().Criar);

        var ok = logger.Mensagens.Where(m => m.StartsWith("Credencial Google OK")).ToList();
        Assert.Equal(2, ok.Count);
        Assert.Contains("secrets_key=instituto", ok[0]);
        Assert.Contains("secrets_key=ministerio", ok[1]);
        Assert.Contains("projeto=111", ok[0]);
        Assert.Contains("clientId=111-compartilhado.apps.googleusercontent.com", ok[0]);

        string Hash(string m) => m[(m.IndexOf("refreshTokenSha256Prefix=", StringComparison.Ordinal) + "refreshTokenSha256Prefix=".Length)..];
        Assert.Equal(8, Hash(ok[0]).Length);
        Assert.NotEqual(Hash(ok[0]), Hash(ok[1]));

        string[] segredos = [SegredoInstituto, TokenInstituto, "segredo-ministerio-abc", "token-ministerio-abc"];
        Assert.DoesNotContain(logger.Mensagens, m => segredos.Any(m.Contains));
    }
}
