using Giom.VideoManager.Infrastructure.Secrets;
using Microsoft.Extensions.Configuration;

namespace Giom.VideoManager.Infrastructure.Supabase;

public static class ConfigurationBuilderExtensions
{
    public static IConfigurationBuilder AddSupabaseSecrets(this IConfigurationBuilder builder, IConfiguration partialConfig)
    {
        var provider = new SupabaseSecretsProvider(new SupabaseSecretsProviderOptions
        {
            Url = partialConfig["Supabase:Url"]
                ?? throw new InvalidOperationException("Supabase:Url não configurado (necessário para Secrets:Provider=Supabase)"),
            SecretKey = partialConfig["Supabase:SecretKey"]
                ?? throw new InvalidOperationException("Supabase:SecretKey não configurado (necessário para Secrets:Provider=Supabase)"),
            Table = partialConfig["Secrets:Table"] ?? "app_secret"
        });

        return builder.Add(new SecretsConfigurationSource(provider));
    }
}
