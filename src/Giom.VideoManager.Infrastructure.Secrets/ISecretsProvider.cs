namespace Giom.VideoManager.Infrastructure.Secrets;

/// <summary>
/// Abstraction for loading application secrets from any external store.
/// Current implementation: ConfigurationSecretsProvider (reads env vars / user secrets via IConfiguration).
/// Future implementations: AwsParameterStoreSecretsProvider, AzureAppConfigSecretsProvider.
/// </summary>
public interface ISecretsProvider
{
    /// <summary>
    /// Returns secrets as configuration key→value pairs to be merged into IConfiguration.
    /// Return empty if the provider defers to the existing configuration pipeline.
    /// Keys use colon-separated notation: "Claude:ApiKey", "Supabase:SecretKey", etc.
    /// </summary>
    IDictionary<string, string?> Load();
}
