using System.Globalization;
using System.Text;
using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Infrastructure.Helicone;

/// <summary>
/// Traduz o SessaoObservabilidade genérico (ISessionContext) — anunciado por quem chama a API,
/// sem saber que é o Helicone quem está do outro lado — em headers HTTP específicos do
/// Helicone, em cada requisição. Fica na pipeline do HttpClient "anthropic" (via
/// AddHttpMessageHandler) — roda em toda chamada, mas só adiciona headers se algum contexto de
/// sessão realmente foi definido.
/// </summary>
public class HeliconeHeadersHandler(ISessionContext contexto) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var sessao = contexto.Atual;
        if (sessao is not null)
        {
            // SessionName e as Propriedades vêm de dados de negócio (ex.: título do vídeo,
            // nome do canal) — quase sempre acentuados em português. HttpRequestHeaders exige
            // ASCII puro e lança HttpRequestException pra qualquer outro byte; sem isso, TODA
            // chamada ao Claude pra um vídeo com "ã"/"ç"/"é" no título falhava (visto em
            // produção: resumo de praticamente todo vídeo do Instituto quebrando).
            request.Headers.Add("Helicone-Session-Id", ParaAscii(sessao.SessionId));
            request.Headers.Add("Helicone-Session-Name", ParaAscii(sessao.SessionName));
            request.Headers.Add("Helicone-Session-Path", ParaAscii(sessao.SessionPath));

            if (sessao.UserId is not null)
                request.Headers.Add("Helicone-User-Id", ParaAscii(sessao.UserId));

            // Id gerado por quem chamou (não pelo Helicone) — permite reportar feedback depois via
            // POST /v1/request/{id}/feedback sem depender de capturar algo da resposta.
            if (sessao.RequestId is not null)
                request.Headers.Add("Helicone-Request-Id", ParaAscii(sessao.RequestId));

            if (sessao.Propriedades is not null)
                foreach (var (chave, valor) in sessao.Propriedades)
                    request.Headers.Add($"Helicone-Property-{ParaAscii(chave)}", ParaAscii(valor));
        }

        return base.SendAsync(request, ct);
    }

    /// <summary>
    /// Remove acentos (é vira e, ç vira c, ...) preservando legibilidade, e depois descarta
    /// qualquer caractere que ainda não seja ASCII (emoji, outros alfabetos) como rede de
    /// segurança — HttpRequestHeaders não perdoa nenhum byte fora de ASCII.
    /// </summary>
    internal static string ParaAscii(string valor)
    {
        var semAcentos = valor.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(semAcentos.Length);

        foreach (var c in semAcentos)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (c <= 127)
                builder.Append(c);
        }

        return builder.ToString();
    }
}
