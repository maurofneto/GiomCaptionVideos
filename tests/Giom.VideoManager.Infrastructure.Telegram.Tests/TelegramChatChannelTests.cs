using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Testes.Comuns;
using Microsoft.Extensions.Logging;
using Moq;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Telegram.Tests;

/// <summary>
/// Duas classes de bug JÁ aconteceram de verdade neste canal (Board #56/#57: resposta cortada
/// no primeiro bloco de texto, e mensagem estourando os 4096 caracteres do Telegram) — os
/// testes de ResponderAsync existem especificamente pra essas duas nunca
/// voltarem sem que um teste quebre primeiro. A regra de divisão em si virou compartilhada
/// entre canais — ver DivisorDeMensagemTests no projeto Infrastructure.Tests.
/// </summary>
public class TelegramChatChannelTests
{
    private sealed class Ambiente
    {
        public Mock<ITelegramBotClient> Bot { get; } = new();
        public Mock<IBotComandosService> Comandos { get; } = new();
        public FakeConversaHistoricoService Historico { get; } = new();
        public FakeLogger<TelegramChatChannel> Logger { get; } = new();

        public Ambiente()
        {
            Bot.Setup(b => b.SendRequest(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Message());
        }

        public TelegramChatChannel CriarCanal() => new(Bot.Object, Comandos.Object, Historico, Logger);
    }


    // ---------- ResponderAsync ----------

    [Fact]
    public async Task ResponderAsync_TextoCurto_EnviaUmaUnicaMensagemComTeclado()
    {
        var ambiente = new Ambiente();

        await ambiente.CriarCanal().ResponderAsync("123", "resposta curta", avaliacaoId: "turno-1");

        ambiente.Bot.Verify(b => b.SendRequest(
            It.Is<SendMessageRequest>(r => r.ChatId.Identifier == 123 && r.ReplyMarkup != null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResponderAsync_SemAvaliacaoId_NaoAnexaTeclado()
    {
        var ambiente = new Ambiente();

        await ambiente.CriarCanal().ResponderAsync("123", "resposta de comando", avaliacaoId: null);

        ambiente.Bot.Verify(b => b.SendRequest(
            It.Is<SendMessageRequest>(r => r.ReplyMarkup == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResponderAsync_RespostaLonga_DivideEmVariasMensagensESoATeclaAUltima()
    {
        var ambiente = new Ambiente();
        var textoLongo = string.Join("\n\n", Enumerable.Range(1, 10).Select(i => new string('x', 500)));

        await ambiente.CriarCanal().ResponderAsync("123", textoLongo, avaliacaoId: "turno-1");

        ambiente.Bot.Verify(b => b.SendRequest(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()), Times.AtLeast(2));
        ambiente.Bot.Verify(b => b.SendRequest(It.Is<SendMessageRequest>(r => r.ReplyMarkup != null), It.IsAny<CancellationToken>()), Times.Once); // só uma das mensagens leva teclado
    }

    [Fact]
    public async Task ResponderAsync_QuandoHtmlFormatadoFalha_ReenviaComoTextoSimplesSemTags()
    {
        var ambiente = new Ambiente();
        ambiente.Bot.SetupSequence(b => b.SendRequest(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("HTML malformado"))
            .ReturnsAsync(new Message());

        await ambiente.CriarCanal().ResponderAsync("123", "resposta com **negrito**", avaliacaoId: null);

        ambiente.Bot.Verify(b => b.SendRequest(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        ambiente.Bot.Verify(b => b.SendRequest(It.Is<SendMessageRequest>(r => !r.Text.Contains('<')), It.IsAny<CancellationToken>()), Times.Once); // reenvio sem tags HTML
    }

    // ---------- OuvirAsync — log condicional (Bug #128) ----------

    [Fact]
    public async Task OuvirAsync_QuandoChegaAlgumaAtualizacao_LogaAQuantidade()
    {
        var ambiente = new Ambiente();
        var update = new Update { Id = 1, Message = new Message { Chat = new Chat { Id = 123 }, Text = "oi", From = new User { Username = "aluno1" } } };

        ambiente.Bot.SetupSequence(b => b.SendRequest(It.IsAny<GetUpdatesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([update])
            .ThrowsAsync(new OperationCanceledException());

        await foreach (var _ in ambiente.CriarCanal().OuvirAsync(CancellationToken.None)) { }

        Assert.Single(ambiente.Logger.Entradas, e => e.Nivel == LogLevel.Information && e.Mensagem.Contains('1'));
    }

    [Fact]
    public async Task OuvirAsync_PollVazio_NaoLogaNada()
    {
        var ambiente = new Ambiente();

        ambiente.Bot.SetupSequence(b => b.SendRequest(It.IsAny<GetUpdatesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]) // poll vazio — o caso comum, que não pode gerar ruído de log
            .ThrowsAsync(new OperationCanceledException());

        await foreach (var _ in ambiente.CriarCanal().OuvirAsync(CancellationToken.None)) { }

        Assert.Empty(ambiente.Logger.Entradas);
    }

    // ---------- TratarComandoAsync ----------

    [Fact]
    public async Task TratarComandoAsync_QuandoNaoHaEntidadeDeComando_DevolveFalseSemResponder()
    {
        var ambiente = new Ambiente();
        var mensagem = new Message { Chat = new Chat { Id = 123 } }; // sem Entities — não é comando

        var tratado = await ambiente.CriarCanal().TratarComandoAsync(mensagem, "123", "isso não é um comando", CancellationToken.None);

        Assert.False(tratado);
        ambiente.Bot.Verify(b => b.SendRequest(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Message CriarMensagemDeComando(string texto) => new()
    {
        Chat = new Chat { Id = 123 },
        Entities = [new MessageEntity { Type = MessageEntityType.BotCommand, Offset = 0, Length = texto.Split(' ')[0].Length }]
    };

    [Fact]
    public async Task TratarComandoAsync_ComandoAjuda_ChamaObterAjudaEResponde()
    {
        var ambiente = new Ambiente();
        ambiente.Comandos.Setup(c => c.ObterAjuda()).Returns("aqui está a ajuda");

        var tratado = await ambiente.CriarCanal().TratarComandoAsync(CriarMensagemDeComando("/ajuda"), "123", "/ajuda", CancellationToken.None);

        Assert.True(tratado);
        ambiente.Comandos.Verify(c => c.ObterAjuda(), Times.Once);
        ambiente.Bot.Verify(b => b.SendRequest(It.Is<SendMessageRequest>(r => r.Text.Contains("ajuda")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TratarComandoAsync_ComandoLimpar_ChamaLimparAsyncComOChatId()
    {
        var ambiente = new Ambiente();
        ambiente.Comandos.Setup(c => c.LimparAsync("123", It.IsAny<CancellationToken>())).ReturnsAsync("histórico apagado");

        await ambiente.CriarCanal().TratarComandoAsync(CriarMensagemDeComando("/limpar"), "123", "/limpar", CancellationToken.None);

        ambiente.Comandos.Verify(c => c.LimparAsync("123", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TratarComandoAsync_ComandoDesconhecido_RespondeSemChamarComandosService()
    {
        var ambiente = new Ambiente();

        var tratado = await ambiente.CriarCanal().TratarComandoAsync(CriarMensagemDeComando("/naoexiste"), "123", "/naoexiste", CancellationToken.None);

        Assert.True(tratado); // ainda assim "tratou" o comando — só respondeu que não conhece
        ambiente.Bot.Verify(b => b.SendRequest(It.Is<SendMessageRequest>(r => r.Text.Contains("Não conheço")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TratarComandoAsync_ComandoComSufixoDeGrupo_IgnoraOSufixoArrobaNomeDoBot()
    {
        var ambiente = new Ambiente();
        ambiente.Comandos.Setup(c => c.ObterAjuda()).Returns("ajuda");
        var texto = "/ajuda@MeuBot";
        var mensagem = new Message { Chat = new Chat { Id = 123 }, Entities = [new MessageEntity { Type = MessageEntityType.BotCommand, Offset = 0, Length = texto.Length }] };

        await ambiente.CriarCanal().TratarComandoAsync(mensagem, "123", texto, CancellationToken.None);

        ambiente.Comandos.Verify(c => c.ObterAjuda(), Times.Once); // reconheceu "/ajuda" mesmo com "@MeuBot" grudado
    }

    // ---------- TratarCallbackFeedbackAsync (via OuvirAsync) ----------

    [Fact]
    public async Task OuvirAsync_CallbackDeFeedbackUtil_RegistraFeedbackEResponeAoCallback()
    {
        var ambiente = new Ambiente();
        var update = new Update
        {
            Id = 1,
            CallbackQuery = new CallbackQuery
            {
                Id = "cb-1",
                Data = "fb:turno-1:1",
                Message = new Message { Chat = new Chat { Id = 123 } }
            }
        };

        ambiente.Bot.SetupSequence(b => b.SendRequest(It.IsAny<GetUpdatesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([update])
            .ThrowsAsync(new OperationCanceledException());

        await foreach (var _ in ambiente.CriarCanal().OuvirAsync(CancellationToken.None)) { }

        Assert.Single(ambiente.Historico.Feedbacks, f => f.ConversaId == "turno-1" && f.Util);
        ambiente.Bot.Verify(b => b.SendRequest(It.IsAny<AnswerCallbackQueryRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        ambiente.Bot.Verify(b => b.SendRequest(It.IsAny<EditMessageReplyMarkupRequest>(), It.IsAny<CancellationToken>()), Times.Once); // remove os botões pra não votar de novo
    }

    [Fact]
    public async Task OuvirAsync_CallbackDeFeedbackNaoUtil_RegistraComUtilFalse()
    {
        var ambiente = new Ambiente();
        var update = new Update { Id = 1, CallbackQuery = new CallbackQuery { Id = "cb-1", Data = "fb:turno-1:-1", Message = new Message { Chat = new Chat { Id = 123 } } } };

        ambiente.Bot.SetupSequence(b => b.SendRequest(It.IsAny<GetUpdatesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([update])
            .ThrowsAsync(new OperationCanceledException());

        await foreach (var _ in ambiente.CriarCanal().OuvirAsync(CancellationToken.None)) { }

        Assert.Single(ambiente.Historico.Feedbacks, f => f.ConversaId == "turno-1" && !f.Util);
    }

    [Fact]
    public async Task OuvirAsync_CallbackComDataEmFormatoInesperado_NaoRegistraFeedbackNemLancaExcecao()
    {
        var ambiente = new Ambiente();
        var update = new Update { Id = 1, CallbackQuery = new CallbackQuery { Id = "cb-1", Data = "fb:formato:invalido:demais", Message = new Message { Chat = new Chat { Id = 123 } } } };

        ambiente.Bot.SetupSequence(b => b.SendRequest(It.IsAny<GetUpdatesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([update])
            .ThrowsAsync(new OperationCanceledException());

        var excecao = await Record.ExceptionAsync(async () =>
        {
            await foreach (var _ in ambiente.CriarCanal().OuvirAsync(CancellationToken.None)) { }
        });

        Assert.Null(excecao);
        Assert.Empty(ambiente.Historico.Feedbacks);
    }

    [Fact]
    public async Task OuvirAsync_MensagemDeTextoNormal_NaoTratadaComoComando_EhEmitidaComoIncomingMessage()
    {
        var ambiente = new Ambiente();
        var update = new Update
        {
            Id = 1,
            Message = new Message { Chat = new Chat { Id = 123 }, Text = "O que é escatologia?", From = new User { Username = "aluno1" } }
        };

        ambiente.Bot.SetupSequence(b => b.SendRequest(It.IsAny<GetUpdatesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([update])
            .ThrowsAsync(new OperationCanceledException());

        var recebidas = new List<IncomingMessage>();
        await foreach (var mensagem in ambiente.CriarCanal().OuvirAsync(CancellationToken.None))
            recebidas.Add(mensagem);

        var unica = Assert.Single(recebidas);
        Assert.Equal("123", unica.ChatId);
        Assert.Equal("O que é escatologia?", unica.Texto);
        Assert.Equal("aluno1", unica.UserName);
    }
}
