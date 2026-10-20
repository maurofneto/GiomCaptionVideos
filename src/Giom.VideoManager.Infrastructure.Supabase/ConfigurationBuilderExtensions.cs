using Giom.VideoManager.Infrastructure.Secrets;
using Microsoft.Extensions.Configuration;

namespace Giom.VideoManager.Infrastructure.Supabase;

public static class ConfigurationBuilderExtensions
{
    public static IConfigurationBuilder AddSupabaseSecrets(this IConfigurationBuilder builder, IConfiguration partialConfig)
    {
        var dbOptions = partialConfig.GetSection("Supabase").Get<SupabaseOptions>() ?? new SupabaseOptions()
        {
            Url = partialConfig["Supabase:Url"]
                ?? throw new InvalidOperationException("Supabase:Url não configurado (necessário para Secrets:Provider=Supabase)"),
            SecretKey = partialConfig["Supabase:SecretKey"]
                ?? throw new InvalidOperationException("Supabase:SecretKey não configurado (necessário para Secrets:Provider=Supabase)")
        };
        var provider = new SupabaseSecretsProvider(new SupabaseSecretsProviderOptions
        {
            Url = dbOptions.Url,
            SecretKey = dbOptions.SecretKey,
            Table = partialConfig["Secrets:Table"] ?? "app_secret"
        }, Microsoft.Extensions.Options.Options.Create(dbOptions));

        return builder.Add(new SecretsConfigurationSource(provider));
    }
}
