using Amazon;
using Amazon.SQS;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Infrastructure.Sqs;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra uma fila de etapa (Feature #33 — Transcrição/Resumo/Categorização/Embedding, todas
    /// tratadas igual, sem caso especial) como Keyed Service (.NET 9) — várias filas do mesmo tipo
    /// de mensagem (VideoEtapaMensagem) coexistindo sem colidir, cada uma com seu próprio
    /// SqsFilaCanal/QueueName. TryAddSingleton no IAmazonSQS — um cliente só, reutilizado por
    /// todas as chamadas a este método (uma por etapa). Fila se auto-cria no primeiro uso — não
    /// precisa provisionar via console/CLI antes.
    /// </summary>
    public static IServiceCollection AddSqsFilaEtapa(this IServiceCollection services, string chave, IConfiguration config, string sectionPath)
    {
        var options = config.GetSection(sectionPath).Get<SqsOptions>() ?? new SqsOptions();
        var iOptions = Microsoft.Extensions.Options.Options.Create(options);

        services.TryAddSingleton<IAmazonSQS>(_ =>
            new AmazonSQSClient(RegionEndpoint.GetBySystemName(options.Region)));

        services.AddKeyedSingleton<SqsFilaCanal>(chave, (sp, _) =>
            new SqsFilaCanal(sp.GetRequiredService<IAmazonSQS>(), iOptions, sp.GetRequiredService<ILogger<SqsFilaCanal>>()));

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
