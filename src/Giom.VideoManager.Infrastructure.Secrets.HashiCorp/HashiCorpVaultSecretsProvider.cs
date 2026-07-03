using VaultSharp;
using VaultSharp.V1.AuthMethods;
using VaultSharp.V1.AuthMethods.Token;

namespace Giom.VideoManager.Infrastructure.Secrets.HashiCorp;

public class HashiCorpVaultOptions
{
    public string Address { get; init; } = "http://127.0.0.1:8200";
    public string Token { get; init; } = string.Empty;

    /// <summary>Mount point do secrets engine KV v2 (padrão: "secret").</summary>
    public string MountPoint { get; init; } = "secret";

    /// <summary>Caminho do secret dentro do mount (ex: "giom/instituto").</summary>
    public string Path { get; init; } = "giom";
}

/// <summary>
/// Carrega secrets de um HashiCorp Vault (secrets engine KV v2).
/// Lê um único secret em Path, onde cada campo do JSON mapeia diretamente para
/// uma chave de config (ex: campo "Claude:ApiKey" dentro do secret).
/// </summary>
public class HashiCorpVaultSecretsProvider : ISecretsProvider
{
    private readonly HashiCorpVaultOptions _options;

    public HashiCorpVaultSecretsProvider(HashiCorpVaultOptions options) => _options = options;

    public IDictionary<string, string?> Load()
    {
        IAuthMethodInfo authMethod = new TokenAuthMethodInfo(_options.Token);
        var settings = new VaultClientSettings(_options.Address, authMethod);
        var client = new VaultClient(settings);

        var secret = client.V1.Secrets.KeyValue.V2
            .ReadSecretAsync(path: _options.Path, mountPoint: _options.MountPoint)
            .GetAwaiter().GetResult();

        return secret.Data.Data.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString());
    }
}
