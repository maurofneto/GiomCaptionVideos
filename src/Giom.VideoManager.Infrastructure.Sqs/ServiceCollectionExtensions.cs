using Amazon;
using Amazon.SQS;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

    /// <summary>
    /// Registra uma fila de etapa (Feature #33 — Resumo/Categorização/Embedding) como Keyed
    /// Service (.NET 9) — várias filas do mesmo tipo de mensagem (VideoEtapaMensagem) coexistindo
    /// sem colidir, cada uma com seu próprio SqsFilaCanal/QueueName. Reaproveita o mesmo
    /// IAmazonSQS singleton já registrado por AddSqsFila (mesma região, cliente reutilizável).
    /// Fila se auto-cria no primeiro uso, igual à fila de ingestão.
    /// </summary>
    public static IServiceCollection AddSqsFilaEtapa(this IServiceCollection services, string chave, IConfiguration config, string sectionPath)
    {
        var options = config.GetSection(sectionPath).Get<SqsOptions>() ?? new SqsOptions();
        var iOptions = Microsoft.Extensions.Options.Options.Create(options);

        services.AddKeyedSingleton<SqsFilaCanal>(chave, (sp, _) => new SqsFilaCanal(sp.GetRequiredService<IAmazonSQS>(), iOptions));

        services.AddKeyedSingleton<IFilaPublisher<VideoEtapaMensagem>>(chave, (sp, key) =>
            new SqsFilaPublisher<VideoEtapaMensagem>(sp.GetRequiredService<IAmazonSQS>(), sp.GetRequiredKeyedService<SqsFilaCanal>(key)));

        services.AddKeyedSingleton<IFilaConsumer<VideoEtapaMensagem>>(chave, (sp, key) =>
            new SqsFilaConsumer<VideoEtapaMensagem>(
                sp.GetRequiredService<IAmazonSQS>(),
                sp.GetRequiredKeyedService<SqsFilaCanal>(key),
                iOptions,
                sp.GetRequiredService<ILogger<SqsFilaConsumer<VideoEtapaMensagem>>>()));

        return services;
    }
}
