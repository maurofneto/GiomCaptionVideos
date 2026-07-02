using Amazon;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

namespace Infrastructure.Secrets;

public class AwsParameterStoreOptions
{
    public string Region { get; init; } = "us-east-1";

    /// <summary>Prefixo hierárquico dos parâmetros (ex: "/giom/instituto/").</summary>
    public string Path { get; init; } = "/giom/";
}

/// <summary>
/// Carrega secrets do AWS Systems Manager Parameter Store (tier Standard — gratuito).
/// Lê recursivamente todos os parâmetros sob Path com descriptografia, convertendo
/// "/giom/instituto/Claude/ApiKey" na chave de config "Claude:ApiKey".
/// Credenciais AWS vêm do ambiente (IAM role, env vars ou ~/.aws/credentials) — nunca
/// são passadas explicitamente aqui.
/// </summary>
public class AwsParameterStoreSecretsProvider : ISecretsProvider
{
    private readonly AwsParameterStoreOptions _options;

    public AwsParameterStoreSecretsProvider(AwsParameterStoreOptions options) => _options = options;

    public IDictionary<string, string?> Load()
    {
        using var client = new AmazonSimpleSystemsManagementClient(RegionEndpoint.GetBySystemName(_options.Region));
        var result = new Dictionary<string, string?>();
        string? nextToken = null;

        do
        {
            var response = client.GetParametersByPathAsync(new GetParametersByPathRequest
            {
                Path           = _options.Path,
                Recursive      = true,
                WithDecryption = true,
                NextToken      = nextToken
            }).GetAwaiter().GetResult();

            foreach (var param in response.Parameters)
            {
                var key = param.Name[_options.Path.Length..].Replace('/', ':');
                result[key] = param.Value;
            }

            nextToken = response.NextToken;
        } while (!string.IsNullOrEmpty(nextToken));

        return result;
    }
}
