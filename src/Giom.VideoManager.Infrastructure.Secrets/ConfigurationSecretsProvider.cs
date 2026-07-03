namespace Giom.VideoManager.Infrastructure.Secrets;

/// <summary>
/// Default secrets provider for local development and environments where secrets come
/// from environment variables, user secrets, or appsettings.json.
/// Returns empty — values are already present in the IConfiguration pipeline.
/// </summary>
public class ConfigurationSecretsProvider : ISecretsProvider
{
    public IDictionary<string, string?> Load() => new Dictionary<string, string?>();
}
