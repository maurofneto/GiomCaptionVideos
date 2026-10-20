using System.Net;
using System.Text.Json;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Helicone;
using Giom.VideoManager.Testes.Comuns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Anthropic.Tests;

/// <summary>
/// Cobre o limite de tamanho da resposta do bot (ClaudeAgent:MaxTokens; a instrução de concisão vem de agente_config via IAgenteConfigService)
/// olhando o corpo exato enviado à API do Anthropic — sem chave nem chamada real. O agent.md é
/// o arquivo real do AgenteInstituto.Worker (linkado no csproj), então "prompt inalterado" aqui
/// significa byte-idêntico ao que vai pra produção.
/// </summary>
public class ClaudeAgentServiceTests
{
    private static readonly string AgentMd = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Prompts", "agent.md"));

    private const string RespostaPadrao = """
        {
          "id": "msg_teste",
          "type": "message",
          "role": "assistant",
          "model": "claude-sonnet-5",
          "content": [{ "type": "text", "text": "Resposta do bot." }],
          "stop_reason": "end_turn",
          "usage": { "input_tokens": 100, "output_tokens": 20, "cache_creation_input_tokens": 30, "cache_read_input_tokens": 70 }
        }
        """;

    private sealed class AnthropicFake(string resposta) : HttpMessageHandler
    {
        public List<JsonDocument> CorposEnviados { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CorposEnviados.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(resposta) };
        }

        public IHttpClientFactory ComoFactory() => new FactoryFake(this);

        private sealed class FactoryFake(HttpMessageHandler handler) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
        }
    }

    private sealed class SemIncerteza : IIncertezaDetectorService
    {
        public bool ContemIncerteza(string texto) => false;
    }

    private sealed class PrecoZero : IPricingService
    {
        public Task<decimal?> CalcularAsync(string provedor, string? modelo, int? tokensEntrada, int? tokensSaida = null, CancellationToken ct = default) =>
            Task.FromResult<decimal?>(0m);
    }

    private sealed record Cenario(ClaudeAgentService Service, AnthropicFake Http, FakeContextoTelemetria Telemetria, FakeLogger<ClaudeAgentService> Logger);

    private static Cenario Criar(ClaudeAgentOptions options, string resposta = RespostaPadrao, string instrucaoConcisao = "")
    {
        var http = new AnthropicFake(resposta);
        var telemetria = new FakeContextoTelemetria();
        var logger = new FakeLogger<ClaudeAgentService>();
        var service = new ClaudeAgentService(
            Options.Create(options),
            Options.Create(new HeliconeOptions()),
            new PromptLoader(),
            http.ComoFactory(),
            logger,
            telemetria,
            new SemIncerteza(),
            new FakeSessionContext(),
            new PrecoZero(),
            new FakeAgenteConfigService(instrucaoConcisao));
        return new Cenario(service, http, telemetria, logger);
    }

    private static async Task<(JsonElement Corpo, MetadadosTelemetria Metadados)> ResponderAsync(Cenario cenario)
    {
        var metadados = cenario.Telemetria.IniciarEscopo();
        await cenario.Service.ResponderAsync("chat-1", new TurnoConversa(null, "O que é fé?", null), "contexto das aulas", []);
        return (Assert.Single(cenario.Http.CorposEnviados).RootElement, metadados);
    }

    private static string SystemEnviado(JsonElement corpo) =>
        string.Concat(corpo.GetProperty("system").EnumerateArray().Select(b => b.GetProperty("text").GetString()));

    [Fact]
    public void Options_Padrao_MaxTokens4096()
    {
        var options = new ClaudeAgentOptions();

        Assert.Equal(4096, options.MaxTokens);
    }

    [Fact]
    public async Task ResponderAsync_OptionsPadrao_EnviaMaxTokens4096ESystemPromptIdenticoAoAgentMd()
    {
        var (corpo, metadados) = await ResponderAsync(Criar(new ClaudeAgentOptions()));

        Assert.Equal(4096, corpo.GetProperty("max_tokens").GetInt32());
        Assert.Equal(AgentMd, SystemEnviado(corpo));

        var detalhes = JsonSerializer.SerializeToElement(metadados.Detalhes);
        Assert.Equal(4096, detalhes.GetProperty("maxTokens").GetInt32());
        Assert.False(detalhes.GetProperty("concisao").GetBoolean());
    }

    [Fact]
    public async Task ResponderAsync_InstrucaoConcisaoConfigurada_AnexaSecaoNoFimDoSystemPrompt()
    {
        var (corpo, metadados) = await ResponderAsync(Criar(new ClaudeAgentOptions(), instrucaoConcisao: "Responda em no máximo 3 parágrafos."));

        Assert.Equal(AgentMd + "\n\n# Tamanho da resposta\nResponda em no máximo 3 parágrafos.", SystemEnviado(corpo));
        Assert.True(JsonSerializer.SerializeToElement(metadados.Detalhes).GetProperty("concisao").GetBoolean());
    }

    [Fact]
    public async Task ResponderAsync_MaxTokensConfigurado_EnviaValorConfigurado()
    {
        var (corpo, metadados) = await ResponderAsync(Criar(new ClaudeAgentOptions { MaxTokens = 1200 }));

        Assert.Equal(1200, corpo.GetProperty("max_tokens").GetInt32());
        Assert.Equal(1200, JsonSerializer.SerializeToElement(metadados.Detalhes).GetProperty("maxTokens").GetInt32());
    }

    [Fact]
    public async Task ResponderAsync_RegistraCamposDeCacheDoUsageNaTelemetria()
    {
        var (_, metadados) = await ResponderAsync(Criar(new ClaudeAgentOptions()));

        var detalhes = JsonSerializer.SerializeToElement(metadados.Detalhes);
        Assert.Equal(30, detalhes.GetProperty("cacheCreationInputTokens").GetInt32());
        Assert.Equal(70, detalhes.GetProperty("cacheReadInputTokens").GetInt32());
    }

    [Fact]
    public async Task ResponderAsync_StopReasonMaxTokens_LogaWarningERegistraMaxTokens()
    {
        var cenario = Criar(new ClaudeAgentOptions { MaxTokens = 500 }, RespostaPadrao.Replace("\"end_turn\"", "\"max_tokens\""));

        var (_, metadados) = await ResponderAsync(cenario);

        Assert.Contains(cenario.Logger.Entradas, e => e.Nivel == LogLevel.Warning && e.Mensagem.Contains("cortada por MaxTokens (500)"));
        var detalhes = JsonSerializer.SerializeToElement(metadados.Detalhes);
        Assert.Equal("max_tokens", detalhes.GetProperty("stopReason").GetString());
        Assert.Equal(500, detalhes.GetProperty("maxTokens").GetInt32());
    }

    private static IOptions<ClaudeAgentOptions> ResolverOptions(Dictionary<string, string?> valores)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
        return new ServiceCollection().AddClaudeAgent(config).BuildServiceProvider().GetRequiredService<IOptions<ClaudeAgentOptions>>();
    }

    [Fact]
    public void AddClaudeAgent_SemChaveMaxTokens_UsaPadrao4096()
    {
        Assert.Equal(4096, ResolverOptions(new() { ["ClaudeAgent:Model"] = "claude-sonnet-5" }).Value.MaxTokens);
    }

    [Fact]
    public void AddClaudeAgent_BindaMaxTokensDaConfig()
    {
        var options = ResolverOptions(new() { ["ClaudeAgent:MaxTokens"] = "1500" }).Value;

        Assert.Equal(1500, options.MaxTokens);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void AddClaudeAgent_MaxTokensInvalido_FalhaComMensagemClara(string valor)
    {
        var options = ResolverOptions(new() { ["ClaudeAgent:MaxTokens"] = valor });

        var ex = Assert.Throws<OptionsValidationException>(() => options.Value);
        Assert.Contains("ClaudeAgent:MaxTokens deve ser maior que zero", ex.Message);
    }
}
