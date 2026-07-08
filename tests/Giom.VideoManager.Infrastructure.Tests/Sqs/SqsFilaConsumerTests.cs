using System.Net;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Infrastructure.Sqs;
using Giom.VideoManager.Testes.Comuns;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.Sqs;

public class SqsFilaConsumerTests
{
    private record MensagemDeTeste(string Valor);

    private readonly Mock<IAmazonSQS> _sqs = new();
    private readonly FakeLogger<SqsFilaConsumer<MensagemDeTeste>> _logger = new();
    private readonly SqsFilaConsumer<MensagemDeTeste> _consumer;

    public SqsFilaConsumerTests()
    {
        _sqs.Setup(s => s.CreateQueueAsync(It.IsAny<CreateQueueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateQueueRequest req, CancellationToken _) => new CreateQueueResponse { QueueUrl = $"https://sqs/{req.QueueName}" });
        _sqs.Setup(s => s.GetQueueAttributesAsync(It.IsAny<GetQueueAttributesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetQueueAttributesResponse { Attributes = new() { [QueueAttributeName.QueueArn] = "arn:aws:sqs:us-east-1:123456789012:fila-teste-dlq" } });
        _sqs.Setup(s => s.DeleteMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteMessageResponse());
        _sqs.Setup(s => s.ChangeMessageVisibilityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChangeMessageVisibilityResponse());

        var options = Options.Create(new SqsOptions { QueueName = "fila-teste", MaxReceiveCount = 3, RetryDelaySeconds = 10 });
        _consumer = new SqsFilaConsumer<MensagemDeTeste>(_sqs.Object, new SqsFilaCanal(_sqs.Object, options), options, _logger);
    }

    private void ConfigurarFilaCom(params Message[] mensagens)
    {
        _sqs.SetupSequence(s => s.ReceiveMessageAsync(It.IsAny<ReceiveMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReceiveMessageResponse { Messages = [.. mensagens] })
            .ReturnsAsync(new ReceiveMessageResponse { Messages = [] });
    }

    private static Message NovaMensagem(string valor, string receiptHandle, int tentativa = 1) => new()
    {
        MessageId = $"msg-{valor}",
        Body = JsonSerializer.Serialize(new MensagemDeTeste(valor)),
        ReceiptHandle = receiptHandle,
        Attributes = new() { [MessageSystemAttributeName.ApproximateReceiveCount] = tentativa.ToString() }
    };

    [Fact]
    public async Task Consumir_Sucesso_ChamaHandlerEConfirmaAMensagem()
    {
        ConfigurarFilaCom(NovaMensagem("a", "r1"));
        var processadas = new List<string>();

        await _consumer.ConsumirAsync((m, _) => { processadas.Add(m.Valor); return Task.CompletedTask; });

        Assert.Equal(["a"], processadas);
        _sqs.Verify(s => s.DeleteMessageAsync("https://sqs/fila-teste", "r1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Consumir_FalhaAntesDoLimite_AgendaRetryRapidoSemDeletar()
    {
        ConfigurarFilaCom(NovaMensagem("a", "r1", tentativa: 1));

        await _consumer.ConsumirAsync((_, _) => throw new InvalidOperationException("falha transitória"));

        // Visibilidade reduzida pro RetryDelaySeconds (10s) — retry na mesma run, sem confirmar
        _sqs.Verify(s => s.ChangeMessageVisibilityAsync("https://sqs/fila-teste", "r1", 10, It.IsAny<CancellationToken>()), Times.Once);
        _sqs.Verify(s => s.DeleteMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains(_logger.Entradas, e => e.Nivel == LogLevel.Warning && e.Mensagem.Contains("tentativa 1/3"));
        Assert.DoesNotContain(_logger.Entradas, e => e.Nivel == LogLevel.Error);
    }

    [Fact]
    public async Task Consumir_FalhaNaUltimaTentativa_LogaErrorPraDlqEZeraVisibilidade()
    {
        ConfigurarFilaCom(NovaMensagem("a", "r1", tentativa: 3));

        await _consumer.ConsumirAsync((_, _) => throw new InvalidOperationException("falha permanente"));

        // Visibilidade 0: o próximo receive dispara o move pra DLQ ainda nesta run; o LogError
        // vira alerta no Sentry via sink (US #45)
        _sqs.Verify(s => s.ChangeMessageVisibilityAsync("https://sqs/fila-teste", "r1", 0, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(_logger.Entradas, e => e.Nivel == LogLevel.Error && e.Mensagem.Contains("fila-teste-dlq"));
    }

    [Fact]
    public async Task Consumir_FalhaDeUmaMensagem_NaoImpedeAsDemais()
    {
        ConfigurarFilaCom(NovaMensagem("a", "r1"), NovaMensagem("b", "r2"));

        await _consumer.ConsumirAsync((m, _) =>
            m.Valor == "a" ? throw new InvalidOperationException("só a primeira falha") : Task.CompletedTask);

        _sqs.Verify(s => s.ChangeMessageVisibilityAsync(It.IsAny<string>(), "r1", 10, It.IsAny<CancellationToken>()), Times.Once);
        _sqs.Verify(s => s.DeleteMessageAsync(It.IsAny<string>(), "r2", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Consumir_FalhaComRateLimitSemEsperaSugerida_UsaODelayPadraoDeRateLimit()
    {
        ConfigurarFilaCom(NovaMensagem("a", "r1", tentativa: 1));

        await _consumer.ConsumirAsync((_, _) => throw new RateLimitExcedidoException("YouTube.Teste"));

        // Sem Retry-After conhecido: usa RateLimitDelaySeconds (300s por padrão), não o
        // RetryDelaySeconds (10s) da falha transitória comum — rate limit não some em 10s.
        _sqs.Verify(s => s.ChangeMessageVisibilityAsync("https://sqs/fila-teste", "r1", 300, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(_logger.Entradas, e => e.Nivel == LogLevel.Warning && e.Mensagem.Contains("rate limit"));
    }

    [Fact]
    public async Task Consumir_FalhaComRateLimitComEsperaSugerida_UsaAEsperaDoProvedor()
    {
        ConfigurarFilaCom(NovaMensagem("a", "r1", tentativa: 1));

        await _consumer.ConsumirAsync((_, _) => throw new RateLimitExcedidoException("Claude.Resumo", TimeSpan.FromSeconds(45)));

        // Retry-After informado pelo provedor (ex: Anthropic RateLimits.RetryAfter) tem
        // prioridade sobre o delay padrão configurado.
        _sqs.Verify(s => s.ChangeMessageVisibilityAsync("https://sqs/fila-teste", "r1", 45, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Consumir_FalhaComHttp429Generico_TambemUsaODelayDeRateLimit()
    {
        ConfigurarFilaCom(NovaMensagem("a", "r1", tentativa: 1));

        await _consumer.ConsumirAsync((_, _) => throw new HttpRequestException("blocked", inner: null, statusCode: HttpStatusCode.TooManyRequests));

        // Cobre provedores que ainda não mapeiam pra RateLimitExcedidoException (ex.: OpenAI
        // embeddings, que só lança HttpRequestException com StatusCode).
        _sqs.Verify(s => s.ChangeMessageVisibilityAsync("https://sqs/fila-teste", "r1", 300, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Consumir_RateLimitEmbrulhadaEmOutraExcecao_AindaDetectaPercorrendoInnerException()
    {
        ConfigurarFilaCom(NovaMensagem("a", "r1", tentativa: 1));

        await _consumer.ConsumirAsync((_, _) =>
            throw new InvalidOperationException("decorator relançou", new RateLimitExcedidoException("YouTube.Teste", TimeSpan.FromSeconds(20))));

        // Decorators de telemetria costumam relançar embrulhando a causa original — o delay
        // dinâmico precisa continuar reconhecendo o rate limit lá dentro.
        _sqs.Verify(s => s.ChangeMessageVisibilityAsync("https://sqs/fila-teste", "r1", 20, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Consumir_PedeOApproximateReceiveCountNoReceive()
    {
        ConfigurarFilaCom(NovaMensagem("a", "r1"));

        await _consumer.ConsumirAsync((_, _) => Task.CompletedTask);

        // Sem esse atributo o consumer não sabe em qual tentativa está — o retry/DLQ depende dele
        _sqs.Verify(s => s.ReceiveMessageAsync(It.Is<ReceiveMessageRequest>(r =>
            r.MessageSystemAttributeNames.Contains(MessageSystemAttributeName.ApproximateReceiveCount)), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }
}
