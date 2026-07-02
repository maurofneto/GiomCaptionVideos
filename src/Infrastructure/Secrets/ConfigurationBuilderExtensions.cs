using Microsoft.Extensions.Configuration;

namespace Infrastructure.Secrets;

public static class ConfigurationBuilderExtensions
{
    /// <summary>
    /// Adds a pluggable secrets provider as the last configuration source, allowing it to
    /// override values from appsettings.json.
    /// Reads "Secrets:Provider" from the partial config to choose the implementation.
    /// Accepted values: "Configuration" (default), "Supabase", "AwsParameterStore",
    /// "AzureAppConfiguration", "HashiCorpVault".
    /// </summary>
    public static IConfigurationBuilder AddSecretsProvider(this IConfigurationBuilder builder, IConfiguration partialConfig)
    {
        var providerName = partialConfig["Secrets:Provider"] ?? "Configuration";

        ISecretsProvider provider = providerName switch
        {
            "Configuration" => new ConfigurationSecretsProvider(),

            "Supabase" => new SupabaseSecretsProvider(new SupabaseSecretsProviderOptions
            {
                Url = partialConfig["Supabase:Url"]
                    ?? throw new InvalidOperationException("Supabase:Url não configurado (necessário para Secrets:Provider=Supabase)"),
                SecretKey = partialConfig["Supabase:SecretKey"]
                    ?? throw new InvalidOperationException("Supabase:SecretKey não configurado (necessário para Secrets:Provider=Supabase)"),
                Table = partialConfig["Secrets:Table"] ?? "app_secret"
            }),

            "AwsParameterStore" => new AwsParameterStoreSecretsProvider(new AwsParameterStoreOptions
            {
                Region = partialConfig["Secrets:Aws:Region"] ?? "us-east-1",
                Path   = partialConfig["Secrets:Aws:Path"] ?? "/giom/"
            }),

            "AzureAppConfiguration" => new AzureAppConfigSecretsProvider(new AzureAppConfigOptions
            {
                ConnectionString = partialConfig["Secrets:Azure:ConnectionString"]
                    ?? throw new InvalidOperationException("Secrets:Azure:ConnectionString não configurado (necessário para Secrets:Provider=AzureAppConfiguration)"),
                KeyFilter   = partialConfig["Secrets:Azure:KeyFilter"] ?? "*",
                LabelFilter = partialConfig["Secrets:Azure:LabelFilter"]
            }),

            "HashiCorpVault" => new HashiCorpVaultSecretsProvider(new HashiCorpVaultOptions
            {
                Address    = partialConfig["Secrets:Vault:Address"] ?? "http://127.0.0.1:8200",
                Token      = partialConfig["Secrets:Vault:Token"]
                    ?? throw new InvalidOperationException("Secrets:Vault:Token não configurado (necessário para Secrets:Provider=HashiCorpVault)"),
                MountPoint = partialConfig["Secrets:Vault:MountPoint"] ?? "secret",
                Path       = partialConfig["Secrets:Vault:Path"] ?? "giom"
            }),

            _ => throw new InvalidOperationException(
                $"Secrets provider não reconhecido: '{providerName}'. Valores aceitos: Configuration, Supabase, AwsParameterStore, AzureAppConfiguration, HashiCorpVault")
        };

        return builder.Add(new SecretsConfigurationSource(provider));
    }
}
