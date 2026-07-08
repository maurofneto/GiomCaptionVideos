using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Giom.VideoManager.Infrastructure.Sqs;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.Sqs;

public class SqsFilaCanalTests
{
    private const string ArnDlq = "arn:aws:sqs:us-east-1:123456789012:fila-teste-dlq";

    private readonly Mock<IAmazonSQS> _sqs = new();
    private readonly SqsFilaCanal _canal;

    public SqsFilaCanalTests()
    {
        _sqs.Setup(s => s.CreateQueueAsync(It.IsAny<CreateQueueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateQueueRequest req, CancellationToken _) => new CreateQueueResponse { QueueUrl = $"https://sqs/{req.QueueName}" });
        _sqs.Setup(s => s.GetQueueAttributesAsync(It.IsAny<GetQueueAttributesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetQueueAttributesResponse { Attributes = new() { [QueueAttributeName.QueueArn] = ArnDlq } });

        _canal = new SqsFilaCanal(_sqs.Object, Options.Create(new SqsOptions { QueueName = "fila-teste", MaxReceiveCount = 3, VisibilityTimeout = 300 }));
    }

    [Fact]
    public async Task ResolverQueueUrl_CriaDlqPrimeiroEFilaComRedrivePolicyApontandoPraEla()
    {
        var url = await _canal.ResolverQueueUrlAsync(CancellationToken.None);

        Assert.Equal("https://sqs/fila-teste", url);
        _sqs.Verify(s => s.CreateQueueAsync(It.Is<CreateQueueRequest>(r =>
            r.QueueName == "fila-teste-dlq" && r.Attributes.ContainsKey(QueueAttributeName.MessageRetentionPeriod)), It.IsAny<CancellationToken>()), Times.Once);

        _sqs.Verify(s => s.CreateQueueAsync(It.Is<CreateQueueRequest>(r =>
            r.QueueName == "fila-teste" && RedrivePolicyValida(r)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResolverQueueUrl_FilaJaExisteComAtributosAntigos_ConvergeViaSetQueueAttributes()
    {
        // Simula a fila principal criada antes da Feature #34 (sem RedrivePolicy): CreateQueue
        // com atributos diferentes falha com QueueNameExists — a DLQ (nova) cria normal.
        _sqs.Setup(s => s.CreateQueueAsync(It.Is<CreateQueueRequest>(r => r.QueueName == "fila-teste"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new QueueNameExistsException("atributos diferentes"));
        _sqs.Setup(s => s.GetQueueUrlAsync("fila-teste", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetQueueUrlResponse { QueueUrl = "https://sqs/fila-teste" });
        _sqs.Setup(s => s.SetQueueAttributesAsync(It.IsAny<SetQueueAttributesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SetQueueAttributesResponse());

        var url = await _canal.ResolverQueueUrlAsync(CancellationToken.None);

        Assert.Equal("https://sqs/fila-teste", url);
        _sqs.Verify(s => s.SetQueueAttributesAsync(It.Is<SetQueueAttributesRequest>(r =>
            r.QueueUrl == "https://sqs/fila-teste" && r.Attributes.ContainsKey(QueueAttributeName.RedrivePolicy)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResolverQueueUrl_Cacheia_SegundaChamadaNaoVaiNaAws()
    {
        await _canal.ResolverQueueUrlAsync(CancellationToken.None);
        await _canal.ResolverQueueUrlAsync(CancellationToken.None);

        // 2 CreateQueue no total (DLQ + fila), não 4 — a segunda chamada usa o cache
        _sqs.Verify(s => s.CreateQueueAsync(It.IsAny<CreateQueueRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    private static bool RedrivePolicyValida(CreateQueueRequest request)
    {
        if (!request.Attributes.TryGetValue(QueueAttributeName.RedrivePolicy, out var json)) return false;
        var politica = JsonSerializer.Deserialize<JsonElement>(json);
        return politica.GetProperty("deadLetterTargetArn").GetString() == ArnDlq
            && politica.GetProperty("maxReceiveCount").GetString() == "3";
    }
}
