using System.Net.Http.Headers;

namespace Giom.VideoManager.Infrastructure.Supabase;

/// <summary>
/// Credenciais que um cliente PostgREST manda, decididas num único lugar
/// (<see cref="Resolve"/>) — função pura, usada pelo HttpClient nomeado "supabase" e por quem
/// monta o próprio HttpClient (SupabaseSecretsProvider). Regra:
/// <list type="bullet">
/// <item>RestPrefix preenchido (Supabase cloud, "/rest/v1") + SecretKey: Bearer + apikey, os dois com o SecretKey (prod, não muda).</item>
/// <item>RestPrefix preenchido sem SecretKey: nada (igual a antes).</item>
/// <item>RestPrefix vazio (PostgREST no Railway) + PostgrestToken: só <c>Authorization: Bearer &lt;token&gt;</c>, sem apikey — JWT HS256 com <c>role</c> = giom_pipeline.</item>
/// <item>RestPrefix vazio sem PostgrestToken: nada (leitura anônima, igual a antes).</item>
/// </list>
/// O token nunca aparece em ToString/log: o record sobrescreve ToString.
/// </summary>
public sealed record SupabaseAuthHeaders(string? BearerToken, string? ApiKey)
{
    public static readonly SupabaseAuthHeaders Nenhum = new(null, null);

    public static SupabaseAuthHeaders Resolve(string? restPrefix, string? secretKey, string? postgrestToken)
    {
        if (!string.IsNullOrWhiteSpace(restPrefix))
            return string.IsNullOrWhiteSpace(secretKey) ? Nenhum : new SupabaseAuthHeaders(secretKey, secretKey);

        return string.IsNullOrWhiteSpace(postgrestToken) ? Nenhum : new SupabaseAuthHeaders(postgrestToken, null);
    }

    public void ApplyTo(HttpRequestHeaders headers)
    {
        if (!string.IsNullOrEmpty(BearerToken))
            headers.Authorization = new AuthenticationHeaderValue("Bearer", BearerToken);

        if (!string.IsNullOrEmpty(ApiKey))
            headers.TryAddWithoutValidation("apikey", ApiKey);
    }

    // Record gera ToString com os valores das propriedades — aqui isso vazaria a credencial
    // em qualquer log/exceção que interpole o objeto.
    public override string ToString() =>
        $"{nameof(SupabaseAuthHeaders)} {{ Bearer = {(BearerToken is null ? "none" : "***")}, ApiKey = {(ApiKey is null ? "none" : "***")} }}";
}
