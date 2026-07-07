namespace Giom.VideoManager.Infrastructure.Sqs;

public class SqsOptions
{
    public string QueueUrl { get; init; } = string.Empty;

    /// <summary>Mesmo padrão de AwsParameterStoreOptions — credenciais nunca aqui, resolvidas do ambiente (IAM role/env).</summary>
    public string Region { get; init; } = "us-east-1";

    /// <summary>Long polling — quanto tempo esperar por mensagem antes de devolver vazio.</summary>
    public int WaitTimeSeconds { get; init; } = 20;

    public int MaxNumberOfMessages { get; init; } = 10;

    /// <summary>Tempo (segundos) que uma mensagem recebida fica invisível pra outros consumers — se o handler não confirmar (delete) dentro desse prazo, o SQS reentrega automaticamente.</summary>
    public int VisibilityTimeout { get; init; } = 300;
}
