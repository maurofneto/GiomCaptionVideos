using Giom.VideoManager.Application.Options;
using Giom.VideoManager.Application.Pipeline;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Testes.Comuns;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Giom.VideoManager.Application.Tests.Pipeline;

/// <summary>
/// Cobre o orquestrador do bot (ChatBotProcessor) inteiramente através das portas Domain —
/// nenhum teste aqui conhece Telegram, Claude ou Supabase, só os fakes/mocks das interfaces
/// que o ChatBotProcessor realmente depende. Isso é o que garante que os testes continuam
/// valendo mesmo se o adapter concreto de qualquer uma dessas portas mudar.
/// </summary>
public class ChatBotProcessorTests
{
    private const string ChatId = "12345";

    private static BuscaSemanticaResultado BuscaComChunks(int quantidade = 2) => new(
        Enumerable.Range(1, quantidade)
            .Select(i => new ChunkSimilar($"video-{i}", $"Aula {i}", 0, "conteúdo da aula", "Escatologia", null, 0.8))
            .ToList(),
        "contexto formatado das aulas");

    /// <summary>
    /// Agrupa todos os fakes/mocks e a fábrica do processor num só lugar — evita repetir a
    /// mesma lista de 12 dependências em cada teste, e deixa cada teste sobrescrever só o que
    /// realmente importa pro cenário.
    /// </summary>
    private sealed class Ambiente
    {
        public FakeChatChannel Channel { get; } = new();
        public FakeConversaHistoricoService Historico { get; } = new();
        public FakeTelemetriaService Telemetria { get; } = new();
        public FakeTracingService Tracing { get; } = new();
        public FakeGuardRailPipeline GuardRails { get; } = new();
        public FakeContextoTelemetria ContextoTelemetria { get; } = new();
        public FakeLogger<ChatBotProcessor> Logger { get; } = new();
        public Mock<IBuscaSemanticaService> Embedding { get; } = new();
        public Mock<IAgentService> Agent { get; } = new();

        public RagOptions Rag { get; set; } = new();
        public DebugMetricasOptions Debug { get; set; } = new();
        public HistoricoOptions HistoricoOpcoes { get; set; } = new();
        public BotOptions BotOpcoes { get; set; } = new();

        public Ambiente()
        {
            Embedding
                .Setup(e => e.BuscarAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(BuscaComChunks());

            Agent
                .Setup(a => a.ResponderAsync(It.IsAny<string>(), It.IsAny<TurnoConversa>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<TurnoConversa>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("resposta gerada pelo agente");
        }

        public ChatBotProcessor CriarProcessor() => new(
            Channel, Embedding.Object, Agent.Object, Historico, Telemetria, Tracing, GuardRails, ContextoTelemetria,
            Microsoft.Extensions.Options.Options.Create(Rag), Microsoft.Extensions.Options.Options.Create(Debug), Microsoft.Extensions.Options.Options.Create(HistoricoOpcoes),
            Microsoft.Extensions.Options.Options.Create(BotOpcoes), Logger);
    }

    [Fact]
    public async Task ProcessarMensagemAsync_QuandoGuardRailDeEntradaBloqueia_NaoBuscaNaoChamaAgenteENaoPersisteNoHistorico()
    {
        var ambiente = new Ambiente();
        ambiente.GuardRails.ResultadoEntrada = GuardRailResultado.Bloquear("Não posso ajudar com isso.", "termo bloqueado: teste");
        var processor = ambiente.CriarProcessor();

        await processor.ProcessarMensagemAsync(new IncomingMessage(ChatId, "pergunta suspeita", "usuario"), CancellationToken.None);

        ambiente.Embedding.Verify(e => e.BuscarAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Never);
        ambiente.Agent.Verify(a => a.ResponderAsync(It.IsAny<string>(), It.IsAny<TurnoConversa>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<TurnoConversa>>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(0, ambiente.Historico.VezesAdicionado);

        var resposta = Assert.Single(ambiente.Channel.Respostas);
        Assert.Equal("Não posso ajudar com isso.", resposta.Texto);

        var registro = Assert.Single(ambiente.Telemetria.Registros);
        Assert.Equal("guardrail_bloqueio", registro.Tipo);
        Assert.True(registro.Sucesso);
    }

    [Fact]
    public async Task ProcessarMensagemAsync_FluxoCompleto_BuscaGeraRespondeEPersisteNoHistorico()
    {
        var ambiente = new Ambiente();
        var processor = ambiente.CriarProcessor();

        await processor.ProcessarMensagemAsync(new IncomingMessage(ChatId, "O que é escatologia?", "usuario"), CancellationToken.None);

        ambiente.Embedding.Verify(e => e.BuscarAsync("O que é escatologia?", null, null, 6, 0.35, It.IsAny<CancellationToken>()), Times.Once);
        ambiente.Agent.Verify(a => a.ResponderAsync(ChatId, It.IsAny<TurnoConversa>(), "contexto formatado das aulas", It.IsAny<IReadOnlyList<TurnoConversa>>(), It.IsAny<CancellationToken>()), Times.Once);

        Assert.Equal(1, ambiente.Historico.VezesAdicionado);
        Assert.Equal(1, ambiente.Historico.VezesAtualizado);

        var resposta = Assert.Single(ambiente.Channel.Respostas);
        Assert.Equal("resposta gerada pelo agente", resposta.Texto);
        Assert.Equal("1", resposta.AvaliacaoId); // sem RequestIdExterno, avaliacaoId é só o Id do turno

        Assert.Contains(ambiente.Telemetria.Registros, r => r.Tipo == "busca_rag" && r.Sucesso);
        Assert.Contains(ambiente.Telemetria.Registros, r => r.Tipo == "bot_interacao" && r.Sucesso);
    }

    [Fact]
    public async Task ProcessarMensagemAsync_QuandoAgenteReportaRequestIdExterno_AvaliacaoIdVemComposto()
    {
        var ambiente = new Ambiente();
        ambiente.Agent
            .Setup(a => a.ResponderAsync(It.IsAny<string>(), It.IsAny<TurnoConversa>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<TurnoConversa>>(), It.IsAny<CancellationToken>()))
            .Callback(() => ambiente.ContextoTelemetria.IniciarEscopo(reaproveitarEscopoExistente: true).RequestIdExterno = "helicone-req-abc")
            .ReturnsAsync("resposta gerada pelo agente");
        var processor = ambiente.CriarProcessor();

        await processor.ProcessarMensagemAsync(new IncomingMessage(ChatId, "pergunta qualquer", "usuario"), CancellationToken.None);

        var resposta = Assert.Single(ambiente.Channel.Respostas);
        Assert.Equal("1|helicone-req-abc", resposta.AvaliacaoId);
    }

    [Fact]
    public async Task ProcessarMensagemAsync_QuandoGuardRailDeSaidaBloqueia_EnviaEPersisteASubstitutaNaoAOriginal()
    {
        var ambiente = new Ambiente();
        ambiente.GuardRails.ResultadoSaida = GuardRailResultado.Bloquear("Resposta genérica de segurança.", "reprovado pelo avaliador de IA");
        var processor = ambiente.CriarProcessor();

        await processor.ProcessarMensagemAsync(new IncomingMessage(ChatId, "pergunta qualquer", "usuario"), CancellationToken.None);

        var resposta = Assert.Single(ambiente.Channel.Respostas);
        Assert.Equal("Resposta genérica de segurança.", resposta.Texto);

        var historico = await ambiente.Historico.ObterAsync(ChatId);
        Assert.Equal("Resposta genérica de segurança.", Assert.Single(historico).Resposta);
    }

    [Fact]
    public async Task ProcessarMensagemAsync_HistoricoDentroDoLimite_PassaTodosOsTurnosParaOAgente()
    {
        var ambiente = new Ambiente { HistoricoOpcoes = new HistoricoOptions { LimiteCaracteres = 1000 } };
        ambiente.Historico.Semear(ChatId, ("pergunta antiga 1", "resposta antiga 1"), ("pergunta antiga 2", "resposta antiga 2"));

        IReadOnlyList<TurnoConversa>? historicoRecebido = null;
        ambiente.Agent
            .Setup(a => a.ResponderAsync(It.IsAny<string>(), It.IsAny<TurnoConversa>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<TurnoConversa>>(), It.IsAny<CancellationToken>()))
            .Callback<string, TurnoConversa, string, IReadOnlyList<TurnoConversa>, CancellationToken>((_, _, _, historico, _) => historicoRecebido = historico)
            .ReturnsAsync("resposta nova");

        await ambiente.CriarProcessor().ProcessarMensagemAsync(new IncomingMessage(ChatId, "pergunta nova", "usuario"), CancellationToken.None);

        Assert.NotNull(historicoRecebido);
        Assert.Equal(2, historicoRecebido!.Count);
        Assert.Equal("pergunta antiga 1", historicoRecebido[0].Pergunta); // ordem cronológica preservada
        Assert.Equal("pergunta antiga 2", historicoRecebido[1].Pergunta);
    }

    [Fact]
    public async Task ProcessarMensagemAsync_HistoricoAcimaDoLimite_DescartaOsTurnosMaisAntigosPrimeiro()
    {
        // Cada turno tem ~40 caracteres (pergunta + resposta) — limite de 100 cabe só o
        // turno mais recente e o penúltimo, não os 3 mais antigos.
        var ambiente = new Ambiente { HistoricoOpcoes = new HistoricoOptions { LimiteCaracteres = 100 } };
        ambiente.Historico.Semear(ChatId,
            ("pergunta antiga um bem antiga", "resposta antiga um bem antiga"),
            ("pergunta antiga dois bem antiga", "resposta antiga dois bem antiga"),
            ("pergunta antiga tres bem antiga", "resposta antiga tres bem antiga"),
            ("pergunta recente", "resposta recente"));

        IReadOnlyList<TurnoConversa>? historicoRecebido = null;
        ambiente.Agent
            .Setup(a => a.ResponderAsync(It.IsAny<string>(), It.IsAny<TurnoConversa>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<TurnoConversa>>(), It.IsAny<CancellationToken>()))
            .Callback<string, TurnoConversa, string, IReadOnlyList<TurnoConversa>, CancellationToken>((_, _, _, historico, _) => historicoRecebido = historico)
            .ReturnsAsync("resposta nova");

        await ambiente.CriarProcessor().ProcessarMensagemAsync(new IncomingMessage(ChatId, "pergunta nova", "usuario"), CancellationToken.None);

        Assert.NotNull(historicoRecebido);
        Assert.DoesNotContain(historicoRecebido!, t => t.Pergunta.Contains("antiga um"));
        Assert.Contains(historicoRecebido!, t => t.Pergunta == "pergunta recente"); // o mais recente do histórico anterior sempre sobrevive
    }

    [Fact]
    public async Task ProcessarMensagemAsync_MesmoUmUnicoTurnoAntigoMuitoGrande_NuncaDescartaTodosOsTurnos()
    {
        var ambiente = new Ambiente { HistoricoOpcoes = new HistoricoOptions { LimiteCaracteres = 10 } };
        ambiente.Historico.Semear(ChatId, (new string('x', 500), new string('y', 500)));

        IReadOnlyList<TurnoConversa>? historicoRecebido = null;
        ambiente.Agent
            .Setup(a => a.ResponderAsync(It.IsAny<string>(), It.IsAny<TurnoConversa>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<TurnoConversa>>(), It.IsAny<CancellationToken>()))
            .Callback<string, TurnoConversa, string, IReadOnlyList<TurnoConversa>, CancellationToken>((_, _, _, historico, _) => historicoRecebido = historico)
            .ReturnsAsync("resposta nova");

        await ambiente.CriarProcessor().ProcessarMensagemAsync(new IncomingMessage(ChatId, "pergunta nova", "usuario"), CancellationToken.None);

        Assert.Single(historicoRecebido!); // mantém pelo menos 1, mesmo estourando o limite sozinho
    }

    [Fact]
    public async Task ProcessarMensagemAsync_QuandoDebugMetricasHabilitado_EnviaMensagemDeAcompanhamento()
    {
        var ambiente = new Ambiente { Debug = new DebugMetricasOptions { Habilitado = true, ContextWindowTokens = 1000 } };
        ambiente.Agent
            .Setup(a => a.ResponderAsync(It.IsAny<string>(), It.IsAny<TurnoConversa>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<TurnoConversa>>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                var meta = ambiente.ContextoTelemetria.IniciarEscopo(reaproveitarEscopoExistente: true);
                meta.TokensEntrada = 100;
                meta.TokensSaida = 50;
                meta.CustoEstimadoUsd = 0.0042m;
            })
            .ReturnsAsync("resposta gerada pelo agente");

        await ambiente.CriarProcessor().ProcessarMensagemAsync(new IncomingMessage(ChatId, "pergunta qualquer", "usuario"), CancellationToken.None);

        Assert.Equal(2, ambiente.Channel.Respostas.Count); // resposta real + debug
        var debug = ambiente.Channel.Respostas[1].Texto;
        Assert.Contains("100 entrada", debug);
        Assert.Contains("50 saída", debug);
        Assert.Contains(0.0042m.ToString("F6"), debug); // formatado com a cultura do runtime — não assume separador decimal fixo
    }

    [Fact]
    public async Task ProcessarMensagemAsync_QuandoDebugMetricasDesabilitado_NaoEnviaMensagemDeAcompanhamento()
    {
        var ambiente = new Ambiente { Debug = new DebugMetricasOptions { Habilitado = false } };

        await ambiente.CriarProcessor().ProcessarMensagemAsync(new IncomingMessage(ChatId, "pergunta qualquer", "usuario"), CancellationToken.None);

        Assert.Single(ambiente.Channel.Respostas); // só a resposta real
    }

    [Fact]
    public async Task ProcessarMensagemAsync_QuandoAgenteFalha_RegistraBotInteracaoComoFalhaEUsaIdDoTurnoJaPersistido()
    {
        var ambiente = new Ambiente();
        ambiente.Agent
            .Setup(a => a.ResponderAsync(It.IsAny<string>(), It.IsAny<TurnoConversa>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<TurnoConversa>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Claude indisponível"));

        await ambiente.CriarProcessor().ProcessarMensagemAsync(new IncomingMessage(ChatId, "pergunta qualquer", "usuario"), CancellationToken.None);

        var registro = Assert.Single(ambiente.Telemetria.Registros, r => r.Tipo == "bot_interacao");
        Assert.False(registro.Sucesso);
        Assert.Equal("1", registro.EntidadeId); // pergunta já tinha sido persistida antes da falha

        // Uma falha não pode deixar o aluno sem resposta nenhuma — o bot manda a mensagem de
        // erro configurada em vez de simplesmente ficar em silêncio.
        var resposta = Assert.Single(ambiente.Channel.Respostas);
        Assert.Equal(ambiente.BotOpcoes.MensagemErro, resposta.Texto);
    }

    [Fact]
    public async Task ProcessarMensagemAsync_QuandoFalhaAntesDoTurnoExistir_UsaChatIdComoEntidadeIdDeFallback()
    {
        // AdicionarAsync é a PRIMEIRA coisa que acontece dentro do try (antes até da busca RAG) —
        // é o único jeito de "turno" continuar null quando a exceção é capturada, então é a única
        // falha que precisa cair de volta pro chatId como EntidadeId.
        var ambiente = new Ambiente();
        ambiente.Historico.FalharAoAdicionar = true;

        await ambiente.CriarProcessor().ProcessarMensagemAsync(new IncomingMessage(ChatId, "pergunta qualquer", "usuario"), CancellationToken.None);

        var registro = Assert.Single(ambiente.Telemetria.Registros, r => r.Tipo == "bot_interacao");
        Assert.False(registro.Sucesso);
        Assert.Equal(ChatId, registro.EntidadeId);
    }

    [Fact]
    public async Task ProcessarMensagemAsync_QuandoBuscaRagFalha_DegradaSemContextoDeAulaEAindaAssimResponde()
    {
        // Uma falha isolada na busca RAG não pode derrubar o turno inteiro — o agente ainda
        // responde (sem contexto de aula), em vez do aluno ficar sem resposta nenhuma por causa
        // de uma falha isolada e recuperável (ex.: OpenAI fora do ar por um instante).
        var ambiente = new Ambiente();
        ambiente.Embedding
            .Setup(e => e.BuscarAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Supabase indisponível"));

        await ambiente.CriarProcessor().ProcessarMensagemAsync(new IncomingMessage(ChatId, "pergunta qualquer", "usuario"), CancellationToken.None);

        var registroBusca = Assert.Single(ambiente.Telemetria.Registros, r => r.Tipo == "busca_rag");
        Assert.False(registroBusca.Sucesso);
        Assert.Equal("1", registroBusca.EntidadeId); // turno já persistido antes da busca

        var registroInteracao = Assert.Single(ambiente.Telemetria.Registros, r => r.Tipo == "bot_interacao");
        Assert.True(registroInteracao.Sucesso); // degradado, mas o turno completou

        ambiente.Agent.Verify(a => a.ResponderAsync(It.IsAny<string>(), It.IsAny<TurnoConversa>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<TurnoConversa>>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Single(ambiente.Channel.Respostas); // resposta do agente ainda chega ao aluno
    }

    [Fact]
    public async Task ProcessarMensagemAsync_QuandoCanceladoPorMensagemSuperseded_NaoRegistraFalhaESoLogaInformacao()
    {
        var ambiente = new Ambiente();
        using var cts = new CancellationTokenSource();
        ambiente.Agent
            .Setup(a => a.ResponderAsync(It.IsAny<string>(), It.IsAny<TurnoConversa>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<TurnoConversa>>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, TurnoConversa _, string _, IReadOnlyList<TurnoConversa> _, CancellationToken ct) =>
            {
                cts.Cancel(); // simula: outra mensagem chegou e cancelou este processamento
                await Task.Delay(Timeout.Infinite, ct);
                return "nunca chega aqui";
            });

        await ambiente.CriarProcessor().ProcessarMensagemAsync(new IncomingMessage(ChatId, "pergunta qualquer", "usuario"), cts.Token);

        Assert.DoesNotContain(ambiente.Telemetria.Registros, r => r.Tipo == "bot_interacao"); // cancelamento não é falha
        Assert.Contains(ambiente.Logger.Entradas, e => e.Nivel == LogLevel.Information && e.Mensagem.Contains("cancelado"));
        Assert.Empty(ambiente.Channel.Respostas);
    }

    [Fact]
    public async Task RunAsync_QuandoChegaMensagemNovaDoMesmoChatAntesDaAnteriorTerminar_CancelaAAnteriorEResponsoALtima()
    {
        var ambiente = new Ambiente();
        var chamadas = 0;

        ambiente.Agent
            .Setup(a => a.ResponderAsync(It.IsAny<string>(), It.IsAny<TurnoConversa>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<TurnoConversa>>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, TurnoConversa _, string _, IReadOnlyList<TurnoConversa> _, CancellationToken ct) =>
            {
                if (Interlocked.Increment(ref chamadas) == 1)
                    await Task.Delay(Timeout.Infinite, ct); // primeira pergunta "trava" até ser cancelada pela segunda

                return "resposta da segunda pergunta";
            });

        ambiente.Channel.Mensagens.Add(new IncomingMessage(ChatId, "primeira pergunta", "usuario"));
        ambiente.Channel.Mensagens.Add(new IncomingMessage(ChatId, "segunda pergunta", "usuario"));
        ambiente.Channel.AtrasoEntreMensagens = TimeSpan.FromMilliseconds(50);

        var processor = ambiente.CriarProcessor();
        using var ctTeste = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await processor.RunAsync(ctTeste.Token);
        await Task.Delay(300, CancellationToken.None); // RunAsync não espera as Tasks fire-and-forget — dá tempo delas terminarem

        Assert.Equal(2, chamadas); // as duas perguntas chegaram a chamar o agente
        var resposta = Assert.Single(ambiente.Channel.Respostas); // só UMA resposta chegou ao usuário
        Assert.Equal("resposta da segunda pergunta", resposta.Texto);
        Assert.DoesNotContain(ambiente.Telemetria.Registros, r => r.Tipo == "bot_interacao" && !r.Sucesso);
    }
}
