using System.Globalization;
using System.Text;

namespace Giom.VideoManager.Infrastructure.YouTube;

/// <summary>
/// GoogleCredentialOptions.ApplicationName vira header HTTP (User-Agent) do cliente Google —
/// precisa ser ASCII. Nomes de canal (canal.Nome) são texto de exibição e podem ter acento
/// ("Ministério Giom"), o que quebra o cliente com FormatException. Em vez de trocar o nome
/// por outra coisa (ex: um slug), remove os diacríticos preservando o nome real do canal.
/// </summary>
public static class ApplicationNameFormatter
{
    public static string ParaAscii(string nome)
    {
        var normalizado = nome.Normalize(NormalizationForm.FormD);
        var semAcento = new StringBuilder();

        foreach (var c in normalizado)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (c <= 127)
                semAcento.Append(c);
        }

        return semAcento.ToString();
    }
}
