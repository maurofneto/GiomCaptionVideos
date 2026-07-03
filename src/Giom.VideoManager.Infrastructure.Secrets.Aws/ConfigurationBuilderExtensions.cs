using Giom.VideoManager.Infrastructure.Secrets;
using Microsoft.Extensions.Configuration;

namespace Giom.VideoManager.Infrastructure.Secrets.Aws;

public static class ConfigurationBuilderExtensions
{
    public static IConfigurationBuilder AddAwsParameterStoreSecrets(this IConfigurationBuilder builder, IConfiguration partialConfig)
    {
        var provider = new AwsParameterStoreSecretsProvider(new AwsParameterStoreOptions
        {
            Region = partialConfig["Secrets:Aws:Region"] ?? "us-east-1",
            Path   = partialConfig["Secrets:Aws:Path"] ?? "/giom/"
        });

        return builder.Add(new SecretsConfigurationSource(provider));
    }
}
