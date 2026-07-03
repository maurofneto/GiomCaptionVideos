using Giom.VideoManager.Infrastructure.Secrets;
using Giom.VideoManager.Infrastructure.Secrets.Aws;
using Giom.VideoManager.Infrastructure.Secrets.Azure;
using Giom.VideoManager.Infrastructure.Secrets.HashiCorp;
using Giom.VideoManager.Infrastructure.Supabase;
using Microsoft.Extensions.Configuration;

namespace Giom.VideoManager.Composition;

public static class ConfigurationBuilderExtensions
{
    /// <summary>
    /// Adiciona um provedor de secrets plugável como última fonte de configuração, permitindo
    /// que sobrescreva valores do appsettings.json. Lê "Secrets:Provider" da config parcial
    /// para escolher a implementação; delega ao ConfigurationBuilderExtensions do projeto adapter.
    /// Valores aceitos: "Configuration" (padrão), "Supabase", "AwsParameterStore",
    /// "AzureAppConfiguration", "HashiCorpVault".
    /// </summary>
    public static IConfigurationBuilder AddSecretsProvider(this IConfigurationBuilder builder, IConfiguration partialConfig)
    {
        var providerName = partialConfig["Secrets:Provider"] ?? "Configuration";

        return providerName switch
        {
            "Supabase" => builder.AddSupabaseSecrets(partialConfig),
            "AwsParameterStore" => builder.AddAwsParameterStoreSecrets(partialConfig),
            "AzureAppConfiguration" => builder.AddAzureAppConfigSecrets(partialConfig),
            "HashiCorpVault" => builder.AddHashiCorpVaultSecrets(partialConfig),
            "Configuration" => builder.AddConfigurationSecrets(),
            _ => throw new InvalidOperationException(
                $"Secrets provider não reconhecido: '{providerName}'. Valores aceitos: Configuration, Supabase, AwsParameterStore, AzureAppConfiguration, HashiCorpVault")
        };
    }
}
