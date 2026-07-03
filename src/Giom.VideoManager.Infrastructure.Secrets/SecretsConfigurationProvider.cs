using Microsoft.Extensions.Configuration;

namespace Giom.VideoManager.Infrastructure.Secrets;

public class SecretsConfigurationProvider : ConfigurationProvider
{
    private readonly ISecretsProvider _secretsProvider;

    public SecretsConfigurationProvider(ISecretsProvider provider) => _secretsProvider = provider;

    public override void Load()
    {
        Data = _secretsProvider.Load()
            .Where(kv => kv.Value != null)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }
}
