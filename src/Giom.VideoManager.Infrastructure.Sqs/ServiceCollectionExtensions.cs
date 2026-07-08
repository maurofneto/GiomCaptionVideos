using Amazon;
using Amazon.SQS;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Giom.VideoManager.Infrastructure.Sqs;

public static class ServiceCollectionExtensions
{
    /// <summary>Fila:Sqs — QueueName/Region. Credenciais resolvidas do ambiente (IAM role/env), nunca aqui. A fila é criada automaticamente se não existir (ver SqsFilaCanal) — não precisa provisionar via console/CLI antes.</summary>
    public static IServiceCollection AddSqsFila(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SqsOptions>(config.GetSection("Fila:Sqs"));

        services.AddSingleton<IAmazonSQS>(sp =>
        {
            var region = config["Fila:Sqs:Region"] ?? "us-east-1";
            return new AmazonSQSClient(RegionEndpoint.GetBySystemName(region));
        });

        services.AddSingleton<SqsFilaCanal>();
        services.AddSingleton<IFilaPublisher<VideoParaProcessar>, SqsFilaPublisher<VideoParaProcessar>>();
        services.AddSingleton<IFilaConsumer<VideoParaProcessar>, SqsFilaConsumer<VideoParaProcessar>>();

        return services;
    }
}
