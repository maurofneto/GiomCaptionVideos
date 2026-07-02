using Microsoft.Extensions.Configuration;

namespace Infrastructure.Secrets;

public class SecretsConfigurationSource : IConfigurationSource
{
    private readonly ISecretsProvider _provider;
    public SecretsConfigurationSource(ISecretsProvider provider) => _provider = provider;
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new SecretsConfigurationProvider(_provider);
}
