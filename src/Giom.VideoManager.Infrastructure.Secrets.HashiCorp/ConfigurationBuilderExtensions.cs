using Giom.VideoManager.Infrastructure.Secrets;
using Microsoft.Extensions.Configuration;

namespace Giom.VideoManager.Infrastructure.Secrets.HashiCorp;

public static class ConfigurationBuilderExtensions
{
    public static IConfigurationBuilder AddHashiCorpVaultSecrets(this IConfigurationBuilder builder, IConfiguration partialConfig)
    {
        var provider = new HashiCorpVaultSecretsProvider(new HashiCorpVaultOptions
        {
            Address    = partialConfig["Secrets:HashiCorp:Address"] ?? "http://127.0.0.1:8200",
            Token      = partialConfig["Secrets:HashiCorp:Token"]
                ?? throw new InvalidOperationException("Secrets:HashiCorp:Token não configurado (necessário para Secrets:Provider=HashiCorpVault)"),
            MountPoint = partialConfig["Secrets:HashiCorp:MountPoint"] ?? "secret",
            Path       = partialConfig["Secrets:HashiCorp:Path"] ?? "giom"
        });

        return builder.Add(new SecretsConfigurationSource(provider));
    }
}
