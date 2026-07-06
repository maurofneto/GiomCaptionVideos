using Microsoft.Extensions.Configuration;

namespace Giom.VideoManager.Infrastructure.Sentry;

/// <summary>
/// O sink do Sentry é declarado no appsettings.json normalmente (Serilog:WriteTo, ao lado de
/// Console/File), com "dsn": "" em branco — o valor real nunca fica em appsettings.json, só
/// no SSM (chave Sentry:Dsn, mesma convenção de qualquer outro secret deste projeto). Esse
/// bridge acha dinamicamente em qual posição do array WriteTo está a entrada "Sentry" (não
/// assume índice fixo — resiste a reordenar/adicionar sinks) e injeta o dsn ali, para que
/// Serilog.ReadFrom.Configuration monte o sink já com o valor certo.
/// Chamar depois de AddSecretsProvider, para que Sentry:Dsn já esteja disponível.
/// </summary>
public static class ConfigurationBuilderSentryExtensions
{
    public static IConfigurationBuilder AddSentryDsnFromSecrets(this IConfigurationBuilder builder)
    {
        var built = builder.Build();
        var dsn = built["Sentry:Dsn"];
        if (string.IsNullOrWhiteSpace(dsn))
            return builder;

        foreach (var entry in built.GetSection("Serilog:WriteTo").GetChildren())
        {
            if (string.Equals(entry["Name"], "Sentry", StringComparison.OrdinalIgnoreCase))
            {
                builder.AddInMemoryCollection(new Dictionary<string, string?> { [$"{entry.Path}:Args:dsn"] = dsn });
                break;
            }
        }

        return builder;
    }
}
