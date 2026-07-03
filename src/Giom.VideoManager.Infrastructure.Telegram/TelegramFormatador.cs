using System.Text.RegularExpressions;

namespace Giom.VideoManager.Infrastructure.Telegram;

/// <summary>
/// Converte markdown simples (o que o agente escreve, sem saber de canal nenhum) para o
/// subconjunto de HTML que o Telegram realmente renderiza. Cada canal é responsável por
/// converter pro seu próprio formato — o agente não deveria conhecer Telegram, WhatsApp ou web.
/// </summary>
public static class TelegramFormatador
{
    private static readonly Regex LinhaSeparadoraTabela = new(@"^\|?[\s:|-]+\|?$", RegexOptions.Compiled);
    private static readonly Regex Negrito = new(@"\*\*(.+?)\*\*|__(.+?)__", RegexOptions.Compiled);
    private static readonly Regex Italico = new(@"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)|(?<!_)_(?!_)(.+?)(?<!_)_(?!_)", RegexOptions.Compiled);
    private static readonly Regex CodigoInline = new("`(.+?)`", RegexOptions.Compiled);
    private static readonly Regex Header = new(@"^#{1,6}\s*(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    public static string ParaHtml(string markdown)
    {
        // Escapa caracteres reservados de HTML antes de inserir qualquer tag —
        // evita que um "<" ou "&" perdido no texto quebre o parser do Telegram.
        var texto = markdown.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        texto = RemoverTabelas(texto);
        texto = Header.Replace(texto, "<b>$1</b>");
        texto = Negrito.Replace(texto, m => $"<b>{(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)}</b>");
        texto = Italico.Replace(texto, m => $"<i>{(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)}</i>");
        texto = CodigoInline.Replace(texto, "<code>$1</code>");

        return texto;
    }

    // Telegram não renderiza tabelas — converte cada linha em um item de lista simples.
    private static string RemoverTabelas(string texto)
    {
        var linhas = texto.Split('\n');
        var resultado = new List<string>();

        foreach (var linha in linhas)
        {
            var normalizada = linha.Trim();

            if (LinhaSeparadoraTabela.IsMatch(normalizada) && normalizada.Contains('-'))
                continue;

            if (normalizada.StartsWith('|') && normalizada.EndsWith('|'))
            {
                var celulas = normalizada.Trim('|').Split('|').Select(c => c.Trim());
                resultado.Add("• " + string.Join(" — ", celulas));
                continue;
            }

            resultado.Add(linha);
        }

        return string.Join('\n', resultado);
    }
}
