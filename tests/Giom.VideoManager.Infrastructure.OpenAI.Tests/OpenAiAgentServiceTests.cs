using System.Net;
using System.Text.Json;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Testes.Comuns;
using Microsoft.Extensions.Options;
using Xunit;

namespace Giom.VideoManager.Infrastructure.OpenAI.Tests;

/// <summary>
/// Mesmo contrato de ClaudeAgentServiceTests (OpenAiAgent:MaxTokens / InstrucaoConcisao),
/// conferido no corpo exato enviado ao chat completions — sem chave nem chamada real.
/// </summary>
public class OpenAiAgentServiceTests
{
    private static readonly string AgentMd = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Prompts", "agent.md"));

    private sealed class OpenAiFake : HttpMessageHandler
    {
        public List<JsonDocument> CorposEnviados { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CorposEnviados.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "choices": [{ "message": { "content": "Resposta do bot." }, "finish_reason": "stop" }],
                      "usage": { "prompt_tokens": 100, "completion_tokens": 20 }
                    }
                    """)
            };
        }

        public IHttpClientFactory ComoFactory() => new FactoryFake(this);

        private sealed class FactoryFake(HttpMessageHandler handler) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) =>
                new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.openai.com/") };
        }
    }

    private sealed class SemIncerteza : IIncertezaDetectorService
    {
        public bool ContemIncerteza(string texto) => false;
    }

    private static async Task<(JsonElement Corpo, MetadadosTelemetria Metadados)> ResponderAsync(OpenAiAgentOptions options)
    {
        var http = new OpenAiFake();
        var telemetria = new FakeContextoTelemetria();
        var service = new OpenAiAgentService(
            Options.Create(options),
            new PromptLoader(),
            http.ComoFactory(),
            new FakeLogger<OpenAiAgentService>(),
            telemetria,
            new SemIncerteza(),
            new FakeSessionContext());

        var metadados = telemetria.IniciarEscopo();
        await service.ResponderAsync("chat-1", new TurnoConversa(null, "O que é fé?", null), "contexto das aulas", []);
        return (Assert.Single(http.CorposEnviados).RootElement, metadados);
    }

    private static string SystemEnviado(JsonElement corpo)
    {
        var primeira = corpo.GetProperty("messages")[0];
        Assert.Equal("system", primeira.GetProperty("role").GetString());
        return primeira.GetProperty("content").GetString()!;
    }

    [Fact]
    public async Task ResponderAsync_OptionsPadrao_EnviaMaxTokens4096ESystemPromptIdenticoAoAgentMd()
    {
        var (corpo, metadados) = await ResponderAsync(new OpenAiAgentOptions());

        Assert.Equal(4096, corpo.GetProperty("max_tokens").GetInt32());
        Assert.Equal(AgentMd, SystemEnviado(corpo));

        var detalhes = JsonSerializer.SerializeToElement(metadados.Detalhes);
        Assert.Equal(4096, detalhes.GetProperty("maxTokens").GetInt32());
        Assert.False(detalhes.GetProperty("concisao").GetBoolean());
    }

    [Fact]
    public async Task ResponderAsync_InstrucaoConcisaoConfigurada_AnexaSecaoNoFimDoSystemPrompt()
    {
        var (corpo, metadados) = await ResponderAsync(new OpenAiAgentOptions { InstrucaoConcisao = "Responda em no máximo 3 parágrafos." });

        Assert.Equal(AgentMd + "\n\n# Tamanho da resposta\nResponda em no máximo 3 parágrafos.", SystemEnviado(corpo));
        Assert.True(JsonSerializer.SerializeToElement(metadados.Detalhes).GetProperty("concisao").GetBoolean());
    }

    [Fact]
    public async Task ResponderAsync_MaxTokensConfigurado_EnviaValorConfigurado()
    {
        var (corpo, _) = await ResponderAsync(new OpenAiAgentOptions { MaxTokens = 1200 });

        Assert.Equal(1200, corpo.GetProperty("max_tokens").GetInt32());
    }
}
