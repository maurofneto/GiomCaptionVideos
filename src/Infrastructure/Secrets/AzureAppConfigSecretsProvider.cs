using Azure.Data.AppConfiguration;

namespace Infrastructure.Secrets;

public class AzureAppConfigOptions
{
    public string ConnectionString { get; init; } = string.Empty;
    public string KeyFilter { get; init; } = "*";
    public string? LabelFilter { get; init; }
}

/// <summary>
/// Carrega secrets do Azure App Configuration (tier Free — 10MB, 1k requests/dia).
/// Chaves já usam ":" como separador hierárquico nativo (ex: "Claude:ApiKey"),
/// sem necessidade de tradução.
/// ConnectionString deve apontar para um recurso restrito à identidade do worker —
/// prefira Managed Identity + endpoint no lugar de connection string quando hospedado no Azure.
/// </summary>
public class AzureAppConfigSecretsProvider : ISecretsProvider
{
    private readonly AzureAppConfigOptions _options;

    public AzureAppConfigSecretsProvider(AzureAppConfigOptions options) => _options = options;

    public IDictionary<string, string?> Load()
    {
        var client = new ConfigurationClient(_options.ConnectionString);
        var result = new Dictionary<string, string?>();

        var selector = new SettingSelector { KeyFilter = _options.KeyFilter, LabelFilter = _options.LabelFilter };
        foreach (var setting in client.GetConfigurationSettings(selector))
        {
            result[setting.Key] = setting.Value;
        }

        return result;
    }
}
