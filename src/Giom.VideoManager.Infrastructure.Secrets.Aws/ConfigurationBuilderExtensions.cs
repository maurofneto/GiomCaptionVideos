using Giom.VideoManager.Infrastructure.Secrets;
using Microsoft.Extensions.Configuration;

namespace Giom.VideoManager.Infrastructure.Secrets.Aws;

public static class ConfigurationBuilderExtensions
{
    /// <summary>
    /// Carrega secrets do AWS Parameter Store. Aceita um path único (<c>Secrets:Aws:Path</c>)
    /// ou vários (<c>Secrets:Aws:Paths</c>) — útil quando o worker precisa de secrets de
    /// <c>pipeline/</c> (OpenAI, Helicone, …) e de <c>canais/</c> (OAuth/cookies por canal, US #146).
    /// Se <c>Paths</c> estiver preenchido, ignora <c>Path</c> pra evitar carregar o mesmo prefixo
    /// duas vezes. Ordem: paths na ordem do array (depois sobrescreve chave igual).
    /// </summary>
    public static IConfigurationBuilder AddAwsParameterStoreSecrets(this IConfigurationBuilder builder, IConfiguration partialConfig)
    {
        var region = partialConfig["Secrets:Aws:Region"] ?? "us-east-1";
        var paths = partialConfig.GetSection("Secrets:Aws:Paths").Get<string[]>();

        if (paths is { Length: > 0 })
        {
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                var provider = new AwsParameterStoreSecretsProvider(new AwsParameterStoreOptions
                {
                    Region = region,
                    Path = path
                });
                builder.Add(new SecretsConfigurationSource(provider));
            }

            return builder;
        }

        var single = new AwsParameterStoreSecretsProvider(new AwsParameterStoreOptions
        {
            Region = region,
            Path = partialConfig["Secrets:Aws:Path"] ?? "/giom/"
        });

        return builder.Add(new SecretsConfigurationSource(single));
    }
}
