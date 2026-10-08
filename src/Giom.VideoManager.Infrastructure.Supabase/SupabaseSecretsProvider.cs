using Giom.VideoManager.Infrastructure.Secrets;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Giom.VideoManager.Infrastructure.Supabase;

public class SupabaseSecretsProviderOptions
{
    public string Url { get; init; } = string.Empty;
    public string SecretKey { get; init; } = string.Empty;
    public string Table { get; init; } = "app_secret";
}

/// <summary>
/// Carrega secrets de uma tabela Supabase (app_secret) via PostgREST.
/// Url, SecretKey e PostgrestToken continuam vindo de config/env local — um secrets store não
/// pode fornecer as credenciais necessárias para alcançá-lo (mesma limitação
/// existe com IAM role para AWS Parameter Store ou Managed Identity para Azure). Este provider
/// roda sobre a config PARCIAL (appsettings + env vars, ver AddSecretsProvider): o token não
/// pode vir do SSM — só de appsettings/env (ex.: Supabase__PostgrestToken). Os headers seguem a
/// mesma regra dos demais clientes (<see cref="SupabaseAuthHeaders"/>). Atenção: a role
/// giom_pipeline NÃO tem grant em app_secret (infra/railway/README.md) — com ela este provider
/// leva 403; use-o só com credencial de service_role (cloud, RestPrefix "/rest/v1" + SecretKey).
/// Linhas da tabela usam chaves no formato "Secao:Campo" (ex: "Claude:ApiKey"),
/// que sobrescrevem os valores equivalentes do appsettings.json.
/// </summary>
public class SupabaseSecretsProvider : ISecretsProvider
{
    private readonly SupabaseSecretsProviderOptions _options;
    private readonly SupabaseOptions _dbOptions;

    private readonly HttpMessageHandler? _handler;

    /// <param name="handler">Só pra teste — por padrão o HttpClient usa o handler normal.</param>
    public SupabaseSecretsProvider(SupabaseSecretsProviderOptions options, IOptions<SupabaseOptions> dbOptions, HttpMessageHandler? handler = null)
    {
        _options = options;
        this._dbOptions = dbOptions.Value;
        _handler = handler;
    }

    public IDictionary<string, string?> Load()
    {
        using var http = _handler is null
            ? new HttpClient { BaseAddress = new Uri(_options.Url) }
            : new HttpClient(_handler, disposeHandler: false) { BaseAddress = new Uri(_options.Url) };
        _dbOptions.ResolveAuth().ApplyTo(http.DefaultRequestHeaders);

        var json = http.GetStringAsync(_dbOptions.Rest($"/{_options.Table}?select=key,value")).GetAwaiter().GetResult();
        var rows = JsonConvert.DeserializeObject<List<SecretRow>>(json) ?? [];

        return rows.ToDictionary(r => r.key, r => (string?)r.value);
    }

    private record SecretRow(string key, string value);
}
