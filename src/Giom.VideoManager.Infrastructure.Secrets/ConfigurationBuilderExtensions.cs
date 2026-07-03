using Microsoft.Extensions.Configuration;

namespace Giom.VideoManager.Infrastructure.Secrets;

public static class ConfigurationBuilderExtensions
{
    /// <summary>
    /// No-op secrets provider — values already come from appsettings.json / env vars / user secrets.
    /// </summary>
    public static IConfigurationBuilder AddConfigurationSecrets(this IConfigurationBuilder builder)
        => builder.Add(new SecretsConfigurationSource(new ConfigurationSecretsProvider()));
}
