using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Supabase;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.Supabase;

/// <summary>
/// Todos os clientes PostgREST do projeto Supabase aplicam a MESMA regra de credencial
/// (SupabaseAuthHeaders): os registrados no HttpClient nomeado "supabase" (vários repositórios
/// + SupabaseEmbeddingService) e o SupabaseSecretsProvider, que monta o próprio HttpClient. E
/// o token nunca aparece em log nem em mensagem de exceção. Valores são placeholders.
/// </summary>
public class SupabaseClientesAuthTests
{
    private const string Token = "PLACEHOLDER_POSTGREST_TOKEN";
    private const string Chave = "PLACEHOLDER_SECRET_KEY";

    public sealed record Cenario(string Nome, string RestPrefix, string SecretKey, string Token, string? BearerEsperado, string? ApikeyEsperada);

    // Os 4 cenários do header, vistos de fora (o que chega no fio).
    public static TheoryData<Cenario> Cenarios => new()
    {
        new Cenario("prefixo vazio + token", "", "", Token, Token, null),
        new Cenario("prefixo vazio sem token", "", "", "", null, null),
        new Cenario("rest/v1 + SecretKey", "/rest/v1", Chave, "", Chave, Chave),
        new Cenario("rest/v1 sem SecretKey", "/rest/v1", "", "", null, null),
    };

    // Um cliente por registro Add* — cada um chama o PostgREST uma vez pelo HttpClient "supabase".
    private static readonly string[] Clientes =
    [
        "video", "chunk", "etapa-terminal", "progresso", "conversa-historico", "telemetria",
        "canal-ingestao", "etapa-provedor-config", "pricing", "farol-rpc", "embedding-edge-function",
    ];

    [Theory]
    [MemberData(nameof(Cenarios))]
    public async Task ClientesDoHttpClientSupabase_AplicamARegraDoHeader(Cenario c)
    {
        foreach (var cliente in Clientes)
        {
            var fake = new HttpFake();
            await using var sp = Montar(c, fake, cliente);

            await Chamar(sp, cliente);

            Assert.NotEmpty(fake.Requisicoes); // etapa-terminal faz 2 chamadas (upsert + leitura)
            foreach (var requisicao in fake.Requisicoes)
            {
                Assert.True(requisicao.Authorization == (c.BearerEsperado is null ? null : $"Bearer {c.BearerEsperado}"),
                    $"{cliente} / {c.Nome}: Authorization inesperado");
                Assert.True(requisicao.Apikey == c.ApikeyEsperada, $"{cliente} / {c.Nome}: apikey inesperado");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cenarios))]
    public void SupabaseSecretsProvider_AplicaARegraDoHeader(Cenario c)
    {
        var fake = new HttpFake();
        var dbOptions = new SupabaseOptions { Url = "https://exemplo.invalid", RestPrefix = c.RestPrefix, SecretKey = c.SecretKey, PostgrestToken = c.Token };
        var provider = new SupabaseSecretsProvider(
            new SupabaseSecretsProviderOptions { Url = dbOptions.Url, SecretKey = dbOptions.SecretKey },
            Options.Create(dbOptions), fake);

        var segredos = provider.Load();

        var requisicao = Assert.Single(fake.Requisicoes);
        Assert.Equal(c.BearerEsperado is null ? null : $"Bearer {c.BearerEsperado}", requisicao.Authorization);
        Assert.Equal(c.ApikeyEsperada, requisicao.Apikey);
        Assert.Equal(c.RestPrefix.TrimEnd('/') + "/app_secret?select=key,value", requisicao.PathAndQuery);
        Assert.Empty(segredos);
    }

    [Fact]
    public void SupabaseSecretsProvider_Falha_MensagemDaExcecaoNaoTrazOToken()
    {
        var fake = new HttpFake { Status = HttpStatusCode.Unauthorized };
        var dbOptions = new SupabaseOptions { Url = "https://exemplo.invalid", RestPrefix = "", PostgrestToken = Token };
        var provider = new SupabaseSecretsProvider(new SupabaseSecretsProviderOptions { Url = dbOptions.Url }, Options.Create(dbOptions), fake);

        var ex = Assert.Throws<HttpRequestException>(() => provider.Load());

        Assert.DoesNotContain(Token, ex.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task PostgrestResponde401Ou403_MensagemDaExcecaoNaoTrazOToken(HttpStatusCode status)
    {
        var fake = new HttpFake { Status = status, Corpo = """{"code":"42501","message":"permission denied"}""" };
        var logs = new ColetorDeLogs();
        await using var sp = Montar(new Cenario("token", "", "", Token, Token, null), fake, "video", logs);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            sp.GetRequiredService<IVideoRepository>().UpsertAsync(new Video { Id = "v1" }));

        Assert.Contains(status.ToString(), ex.Message); // a mensagem existe e descreve o status...
        Assert.DoesNotContain(Token, ex.Message);       // ...sem o token
        Assert.DoesNotContain(Token, ex.ToString());
        Assert.DoesNotContain(Token, string.Join("\n", logs.Linhas));

        var provider = new SupabaseSecretsProvider(
            new SupabaseSecretsProviderOptions { Url = "https://exemplo.invalid" },
            Options.Create(new SupabaseOptions { Url = "https://exemplo.invalid", RestPrefix = "", PostgrestToken = Token }),
            new HttpFake { Status = status });
        var exSecrets = Assert.Throws<HttpRequestException>(() => provider.Load());
        Assert.DoesNotContain(Token, exSecrets.Message);
        Assert.DoesNotContain(Token, exSecrets.ToString());
    }

    [Fact]
    public async Task RepositorioDeVideo_FalhaDeUpsert_NemLogNemExcecaoTrazemOToken()
    {
        var fake = new HttpFake { Status = HttpStatusCode.Unauthorized, Corpo = """{"message":"JWSError JWSInvalidSignature"}""" };
        var logs = new ColetorDeLogs();
        await using var sp = Montar(new Cenario("token", "", "", Token, Token, null), fake, "video", logs);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            sp.GetRequiredService<IVideoRepository>().UpsertAsync(new Video { Id = "v1" }));

        Assert.NotEmpty(logs.Linhas); // o erro foi logado...
        Assert.DoesNotContain(Token, string.Join("\n", logs.Linhas)); // ...sem o token
        Assert.DoesNotContain(Token, ex.ToString());
    }

    [Fact]
    public async Task FarolRateLimiter_FalhaDeRpc_NemLogNemExcecaoTrazemOToken()
    {
        var fake = new HttpFake { Status = HttpStatusCode.Unauthorized, Corpo = """{"message":"JWT expired"}""" };
        var logs = new ColetorDeLogs();
        await using var sp = Montar(new Cenario("token", "", "", Token, Token, null), fake, "farol-rpc", logs);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            sp.GetRequiredService<IFarolRateLimiter>().AguardarAsync("VoyageAI", new Dictionary<string, double> { ["requisicoes"] = 1 }));

        Assert.NotEmpty(logs.Linhas);
        Assert.DoesNotContain(Token, string.Join("\n", logs.Linhas));
        Assert.DoesNotContain(Token, ex.ToString());
    }

    [Fact]
    public async Task EmbeddingService_FalhaDaEdgeFunction_NemLogNemExcecaoTrazemOToken()
    {
        var fake = new HttpFake { Status = HttpStatusCode.Unauthorized, Corpo = "unauthorized" };
        var logs = new ColetorDeLogs();
        await using var sp = Montar(new Cenario("token", "", "", Token, Token, null), fake, "embedding-edge-function", logs);
        var servico = sp.GetRequiredService<IEmbeddingService>();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => servico.IndexarAsync(new Video { Id = "v1" }));
        await servico.BuscarAsync("pergunta"); // falha de busca só loga e devolve vazio

        Assert.NotEmpty(logs.Linhas);
        Assert.DoesNotContain(Token, string.Join("\n", logs.Linhas));
        Assert.DoesNotContain(Token, ex.ToString());
    }

    // ---- infraestrutura de teste ----

    private static ServiceProvider Montar(Cenario c, HttpFake fake, string cliente, ColetorDeLogs? logs = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Supabase:Url"] = "https://exemplo.invalid",
            ["Supabase:RestPrefix"] = c.RestPrefix,
            ["Supabase:SecretKey"] = c.SecretKey,
            ["Supabase:PostgrestToken"] = c.Token,
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging(b => { if (logs is not null) b.AddProvider(logs); });

        switch (cliente)
        {
            case "video": case "chunk": case "etapa-terminal": case "progresso":
                services.AddSupabasePersistence(config); break;
            case "conversa-historico": services.AddSupabaseConversaHistorico(config); break;
            case "telemetria": services.AddSupabaseTelemetria(config); break;
            case "canal-ingestao": services.AddSupabaseCanalIngestao(config); break;
            case "etapa-provedor-config": services.AddSupabaseEtapaProvedorConfig(config); break;
            case "pricing": services.AddSupabasePricing(config); break;
            case "farol-rpc": services.AddSupabaseFarolRateLimiter(config); break;
            case "embedding-edge-function": services.AddSupabaseEmbedding(config); break;
            default: throw new ArgumentOutOfRangeException(nameof(cliente), cliente, null);
        }

        // Depois dos Add*: o último ConfigurePrimaryHttpMessageHandler vence. O pipeline de
        // resiliência e o aplicador de headers continuam os de produção.
        services.AddHttpClient("supabase").ConfigurePrimaryHttpMessageHandler(() => fake);
        return services.BuildServiceProvider();
    }

    private static async Task Chamar(IServiceProvider sp, string cliente)
    {
        switch (cliente)
        {
            case "video": await sp.GetRequiredService<IVideoRepository>().GetByIdAsync("v1"); break;
            case "chunk": await sp.GetRequiredService<IVideoChunkRepository>().DeletarPorVideoAsync("v1", "OpenAI"); break;
            case "etapa-terminal": await sp.GetRequiredService<IVideoEtapaTerminalRepository>().RegistrarConclusaoAsync("v1", "embedding"); break;
            case "progresso": await sp.GetRequiredService<IProgressoRepository>().GetLastPageTokenAsync("canal", "playlist"); break;
            case "conversa-historico": await sp.GetRequiredService<IConversaHistoricoService>().RegistrarFeedbackAsync("1", true); break;
            case "telemetria": await sp.GetRequiredService<ITelemetriaSink>().RegistrarAsync(new RegistroTelemetria("teste", "e1", 1, true)); break;
            case "canal-ingestao": await sp.GetRequiredService<ICanalIngestaoRepository>().GetCanaisAtivosAsync(); break;
            case "etapa-provedor-config": await sp.GetRequiredService<IEtapaProvedorConfigRepository>().ObterCadeiaAsync("canal", "resumo"); break;
            case "pricing": await sp.GetRequiredService<IPricingRepository>().ObterAsync("Claude", "modelo"); break;
            case "farol-rpc": await sp.GetRequiredService<IFarolRateLimiter>().AguardarAsync("VoyageAI", new Dictionary<string, double> { ["requisicoes"] = 1 }); break;
            case "embedding-edge-function": await sp.GetRequiredService<IEmbeddingService>().BuscarAsync("pergunta"); break;
            default: throw new ArgumentOutOfRangeException(nameof(cliente), cliente, null);
        }
    }

    private sealed record Requisicao(string Url, string PathAndQuery, string? Authorization, string? Apikey);

    private sealed class HttpFake : HttpMessageHandler
    {
        public List<Requisicao> Requisicoes { get; } = [];
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public string? Corpo { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requisicoes.Add(new Requisicao(
                request.RequestUri!.ToString(),
                request.RequestUri.PathAndQuery,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("apikey", out var v) ? v.Single() : null));

            var caminho = request.RequestUri.AbsolutePath;
            var corpo = Corpo ?? caminho switch
            {
                _ when caminho.EndsWith("/rpc/farol_consumir") => "true",
                _ when caminho.Contains("/functions/v1/") => """{"chunks":[],"contexto_formatado":""}""",
                _ when caminho.EndsWith("/video_etapa_terminal") => "[]",
                _ => "[]",
            };
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(corpo) });
        }
    }

    private sealed class ColetorDeLogs : ILoggerProvider
    {
        public List<string> Linhas { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Coletor(Linhas);
        public void Dispose() { }

        private sealed class Coletor(List<string> linhas) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (linhas) linhas.Add(formatter(state, exception) + (exception is null ? "" : "\n" + exception));
            }
        }
    }
}
