using RabbitMQ.Client;

namespace Giom.VideoManager.Infrastructure.RabbitMq;

/// <summary>
/// Abre conexão+canal e declara a fila (idempotente, durable) — mesma lógica usada tanto pelo
/// publisher quanto pelo consumer, evitando que as duas pontas declarem a fila com flags
/// diferentes por engano. Uma conexão nova por operação (não um pool de longa duração) — volume
/// baixo (um vídeo novo por vez), simplicidade > performance aqui.
/// </summary>
internal static class RabbitMqCanal
{
    public static async Task<RabbitMqSessao> AbrirAsync(RabbitMqOptions options, CancellationToken ct)
    {
        var factory = new ConnectionFactory { Uri = new Uri(options.ConnectionString) };
        var conexao = await factory.CreateConnectionAsync(ct);
        var canal = await conexao.CreateChannelAsync(cancellationToken: ct);

        await canal.QueueDeclareAsync(
            queue: options.QueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            cancellationToken: ct);

        return new RabbitMqSessao(conexao, canal);
    }
}

/// <summary>Agrupa conexão+canal só pra descartar os dois juntos com um único `using`.</summary>
internal sealed class RabbitMqSessao(IConnection conexao, IChannel canal) : IDisposable
{
    private readonly IConnection _conexao = conexao;

    public IChannel Canal { get; } = canal;

    public void Dispose()
    {
        Canal.Dispose();
        _conexao.Dispose();
    }
}
