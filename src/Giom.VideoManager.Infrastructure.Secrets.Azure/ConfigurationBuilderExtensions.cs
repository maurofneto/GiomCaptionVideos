using Giom.VideoManager.Infrastructure.Secrets;
using Microsoft.Extensions.Configuration;

namespace Giom.VideoManager.Infrastructure.Secrets.Azure;

public static class ConfigurationBuilderExtensions
{
    public static IConfigurationBuilder AddAzureAppConfigSecrets(this IConfigurationBuilder builder, IConfiguration partialConfig)
    {
        var provider = new AzureAppConfigSecretsProvider(new AzureAppConfigOptions
        {
            ConnectionString = partialConfig["Secrets:Azure:ConnectionString"]
                ?? throw new InvalidOperationException("Secrets:Azure:ConnectionString não configurado (necessário para Secrets:Provider=AzureAppConfiguration)"),
            KeyFilter   = partialConfig["Secrets:Azure:KeyFilter"] ?? "*",
            LabelFilter = partialConfig["Secrets:Azure:LabelFilter"]
        });

        return builder.Add(new SecretsConfigurationSource(provider));
    }
}
