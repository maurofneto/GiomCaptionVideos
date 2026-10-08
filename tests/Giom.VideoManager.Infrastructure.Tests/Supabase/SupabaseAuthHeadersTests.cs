using Giom.VideoManager.Infrastructure.Supabase;
using System.Net.Http.Headers;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.Supabase;

/// <summary>
/// Regra única de credencial dos clientes PostgREST (SupabaseAuthHeaders.Resolve) — os 4
/// cenários combinados com o Mauro (RestPrefix × SecretKey × PostgrestToken). Os valores são
/// placeholders óbvios, nunca credencial real.
/// </summary>
public class SupabaseAuthHeadersTests
{
    private const string Token = "PLACEHOLDER_POSTGREST_TOKEN";
    private const string Chave = "PLACEHOLDER_SECRET_KEY";

    [Fact]
    public void RestPrefixVazio_ComToken_MandaSoBearerDoToken_SemApikey()
    {
        var auth = SupabaseAuthHeaders.Resolve("", "", Token);

        Assert.Equal(Token, auth.BearerToken);
        Assert.Null(auth.ApiKey);
    }

    [Fact]
    public void RestPrefixVazio_ComToken_IgnoraSecretKey()
    {
        var auth = SupabaseAuthHeaders.Resolve("", Chave, Token);

        Assert.Equal(Token, auth.BearerToken);
        Assert.Null(auth.ApiKey);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("", Chave)]   // hoje: prefixo vazio nunca mandou SecretKey
    [InlineData(null, null)]
    public void RestPrefixVazio_SemToken_NaoMandaNada(string? prefix, string? secretKey)
    {
        Assert.Equal(SupabaseAuthHeaders.Nenhum, SupabaseAuthHeaders.Resolve(prefix, secretKey, ""));
        Assert.Equal(SupabaseAuthHeaders.Nenhum, SupabaseAuthHeaders.Resolve(prefix, secretKey, "   "));
        Assert.Equal(SupabaseAuthHeaders.Nenhum, SupabaseAuthHeaders.Resolve(prefix, secretKey, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData(Token)] // com prefixo, o SecretKey vence — prod não muda
    public void RestPrefixRestV1_ComSecretKey_MandaBearerEApikeyComSecretKey(string token)
    {
        var auth = SupabaseAuthHeaders.Resolve("/rest/v1", Chave, token);

        Assert.Equal(Chave, auth.BearerToken);
        Assert.Equal(Chave, auth.ApiKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData(Token)] // o token só vale com prefixo vazio
    public void RestPrefixRestV1_SemSecretKey_NaoMandaNada(string token)
    {
        Assert.Equal(SupabaseAuthHeaders.Nenhum, SupabaseAuthHeaders.Resolve("/rest/v1", "", token));
    }

    [Fact]
    public void ApplyTo_SoToken_PreencheAuthorization_SemApikey()
    {
        using var req = new HttpRequestMessage();

        SupabaseAuthHeaders.Resolve("", "", Token).ApplyTo(req.Headers);

        Assert.Equal(new AuthenticationHeaderValue("Bearer", Token), req.Headers.Authorization);
        Assert.False(req.Headers.Contains("apikey"));
    }

    [Fact]
    public void ApplyTo_Nenhum_NaoMexeNosHeaders()
    {
        using var req = new HttpRequestMessage();

        SupabaseAuthHeaders.Nenhum.ApplyTo(req.Headers);

        Assert.Null(req.Headers.Authorization);
        Assert.False(req.Headers.Contains("apikey"));
    }

    [Fact]
    public void ToString_NaoExpoeToken_NemSecretKey()
    {
        var token = SupabaseAuthHeaders.Resolve("", "", Token);
        var chave = SupabaseAuthHeaders.Resolve("/rest/v1", Chave, "");

        Assert.DoesNotContain(Token, token.ToString());
        Assert.DoesNotContain(Token, $"{token}");
        Assert.DoesNotContain(Chave, chave.ToString());
    }

    [Fact]
    public void SupabaseOptions_ToString_NaoExpoeToken()
    {
        var options = new SupabaseOptions { Url = "https://exemplo.invalid", RestPrefix = "", PostgrestToken = Token };

        Assert.DoesNotContain(Token, options.ToString());
        Assert.DoesNotContain(Token, options.ResolveAuth().ToString());
    }
}
