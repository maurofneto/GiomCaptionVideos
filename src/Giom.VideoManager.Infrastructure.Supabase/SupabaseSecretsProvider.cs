using Giom.VideoManager.Infrastructure.Secrets;
using Newtonsoft.Json;
using System.Net.Http.Headers;

namespace Giom.VideoManager.Infrastructure.Supabase;

public class SupabaseSecretsProviderOptions
{
    public string Url { get; init; } = string.Empty;
    public string SecretKey { get; init; } = string.Empty;
    public string Table { get; init; } = "app_secret";
}

/// <summary>
/// Carrega secrets de uma tabela Supabase (app_secret) via PostgREST.
/// Url e SecretKey continuam vindo de config/env local — um secrets store não
/// pode fornecer as credenciais necessárias para alcançá-lo (mesma limitação
/// existe com IAM role para AWS Parameter Store ou Managed Identity para Azure).
/// Linhas da tabela usam chaves no formato "Secao:Campo" (ex: "Claude:ApiKey"),
/// que sobrescrevem os valores equivalentes do appsettings.json.
/// </summary>
public class SupabaseSecretsProvider : ISecretsProvider
{
    private readonly SupabaseSecretsProviderOptions _options;

    public SupabaseSecretsProvider(SupabaseSecretsProviderOptions options) => _options = options;

    public IDictionary<string, string?> Load()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_options.Url) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.SecretKey);
        http.DefaultRequestHeaders.Add("apikey", _options.SecretKey);

        var json = http.GetStringAsync($"/rest/v1/{_options.Table}?select=key,value").GetAwaiter().GetResult();
        var rows = JsonConvert.DeserializeObject<List<SecretRow>>(json) ?? [];

        return rows.ToDictionary(r => r.key, r => (string?)r.value);
    }

    private record SecretRow(string key, string value);
}
