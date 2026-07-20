using Amazon;
using Amazon.SQS;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Observabilidade;
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
    /// de mensagem coexistindo sem colidir, cada uma com seu próprio SqsFilaCanal/QueueName.
    /// Genérico em <typeparamref name="TMensagem"/> desde a Feature #104 — a fila de eventos de
    /// conclusão (<c>EtapaConcluidaMensagem</c>) reaproveita exatamente este método, só muda o tipo
    /// da mensagem, não o mecanismo. TryAddSingleton no IAmazonSQS — um cliente só, reutilizado por
    /// todas as chamadas a este método. Fila se auto-cria no primeiro uso — não precisa provisionar
    /// via console/CLI antes.
    /// </summary>
    public static IServiceCollection AddSqsFilaEtapa<TMensagem>(this IServiceCollection services, string chave, IConfiguration config, string sectionPath)
    {
        var options = config.GetSection(sectionPath).Get<SqsOptions>() ?? new SqsOptions();
        var iOptions = Microsoft.Extensions.Options.Options.Create(options);

        services.TryAddSingleton<IAmazonSQS>(_ =>
            new AmazonSQSClient(RegionEndpoint.GetBySystemName(options.Region)));

        // Bug #133: dedupado por TryAddEnumerable — AddSqsFilaEtapa é chamado uma vez por fila
        // (5x numa run típica), mas a regra de exclusão do Sentry só precisa existir uma vez.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISentryTransactionExclusionRule, SqsSentryTransactionExclusionRule>());

        services.AddKeyedSingleton<SqsFilaCanal>(chave, (sp, _) =>
            new SqsFilaCanal(sp.GetRequiredService<IAmazonSQS>(), iOptions, sp.GetRequiredService<ILogger<SqsFilaCanal>>()));

        services.AddKeyedSingleton<IFilaPublisher<TMensagem>>(chave, (sp, key) =>
            new SqsFilaPublisher<TMensagem>(sp.GetRequiredService<IAmazonSQS>(), sp.GetRequiredKeyedService<SqsFilaCanal>(key)));

        services.AddKeyedSingleton<IFilaConsumer<TMensagem>>(chave, (sp, key) =>
            new SqsFilaConsumer<TMensagem>(
                sp.GetRequiredService<IAmazonSQS>(),
                sp.GetRequiredKeyedService<SqsFilaCanal>(key),
                iOptions,
                sp.GetRequiredService<ILogger<SqsFilaConsumer<TMensagem>>>()));

        return services;
    }
}
