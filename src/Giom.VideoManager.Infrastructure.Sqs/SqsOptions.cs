namespace Giom.VideoManager.Infrastructure.Sqs;

public class SqsOptions
{
    /// <summary>Nome da fila (não a URL) — a fila é criada automaticamente se ainda não existir, ver SqsFilaCanal.</summary>
    public string QueueName { get; init; } = string.Empty;

    /// <summary>Mesmo padrão de AwsParameterStoreOptions — credenciais nunca aqui, resolvidas do ambiente (IAM role/env).</summary>
    public string Region { get; init; } = "us-east-1";

    /// <summary>Long polling — quanto tempo esperar por mensagem antes de devolver vazio.</summary>
    public int WaitTimeSeconds { get; init; } = 20;

    public int MaxNumberOfMessages { get; init; } = 10;

    /// <summary>Tempo (segundos) que uma mensagem recebida fica invisível pra outros consumers — se o handler não confirmar (delete) dentro desse prazo, o SQS reentrega automaticamente. Também usado como atributo da fila na criação.</summary>
    public int VisibilityTimeout { get; init; } = 300;

    /// <summary>
    /// Quantas vezes uma mensagem pode ser recebida (tentada) antes do SQS movê-la sozinho pra
    /// DLQ irmã ({QueueName}-dlq) — vira o maxReceiveCount da RedrivePolicy da fila (US #44).
    /// Era 3; subiu pra 6 depois do bug #113: a Lambda watchman (giom-fila-watchman) também faz
    /// ReceiveMessage a cada poll (inerente ao Event Source Mapping) e isso incrementa o MESMO
    /// ApproximateReceiveCount que o worker usa — se a Lambda "inspecionar" a mensagem (achar uma
    /// task já rodando e não processá-la) 3 vezes antes do worker real conseguir recebê-la nem
    /// uma vez, ela ia pra DLQ sem nunca ter sido processada de verdade. O buffer extra dá
    /// margem pra essas inspeções sem enfraquecer demais a proteção contra mensagem realmente
    /// envenenada.
    /// </summary>
    public int MaxReceiveCount { get; init; } = 6;

    /// <summary>
    /// Segundos até uma mensagem que falhou (falha transitória comum) voltar a ficar visível pro
    /// retry. Precisa ser MENOR que WaitTimeSeconds pro retry acontecer na mesma run: o long poll
    /// em andamento entrega a mensagem assim que ela reaparece. Maior que isso, o consumer drena,
    /// vê vazio, sai — e o retry fica pra run seguinte.
    /// </summary>
    public int RetryDelaySeconds { get; init; } = 10;

    /// <summary>
    /// Delay (segundos) quando a falha é RATE LIMIT (RateLimitExcedidoException / HTTP 429) e o
    /// provedor não informou Retry-After — retry rápido contra cota estourada só queima tentativa.
    /// De propósito maior que o long poll: se a fila esvaziar nesse meio-tempo, o retry fica pra
    /// run seguinte (quota diária do YouTube, por exemplo, só volta amanhã mesmo).
    /// </summary>
    public int RateLimitDelaySeconds { get; init; } = 300;
}
