using System.Text.RegularExpressions;

namespace Giom.VideoManager.Infrastructure.WhatsApp;

/// <summary>
/// Converte o markdown simples que o agente escreve (sem saber de canal nenhum) para a
/// marcação própria do WhatsApp. Mesmo papel do TelegramFormatador, alvo diferente: o Telegram
/// renderiza um subconjunto de HTML, o WhatsApp tem sintaxe própria (*negrito*, _itálico_,
/// ~riscado~, ```mono```) e não entende tag nenhuma.
///
/// O detalhe que obriga o uso de marcadores temporários: aqui a sintaxe de ORIGEM e a de
/// DESTINO se sobrepõem. Markdown usa "**x**" pra negrito e "*x*" pra itálico; o WhatsApp usa
/// "*x*" pra negrito. Converter direto faria a regra de itálico casar de novo no "*x*" que a
/// regra de negrito acabou de produzir, embaralhando os dois. Por isso cada conversão grava um
/// caractere de controle (que nunca aparece em texto real) e só no fim eles viram a marcação
/// definitiva.
/// </summary>
public static class WhatsAppFormatador
{
    private const char MarcaNegrito = '\u0001';
    private const char MarcaItalico = '\u0002';
    private const char MarcaMono = '\u0003';

    private static readonly Regex LinhaSeparadoraTabela = new(@"^\|?[\s:|-]+\|?$", RegexOptions.Compiled);
    private static readonly Regex Negrito = new(@"\*\*(.+?)\*\*|__(.+?)__", RegexOptions.Compiled);
    private static readonly Regex Italico = new(@"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)|(?<!_)_(?!_)(.+?)(?<!_)_(?!_)", RegexOptions.Compiled);
    private static readonly Regex CodigoInline = new("`(.+?)`", RegexOptions.Compiled);
    private static readonly Regex Header = new(@"^#{1,6}\s*(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    public static string ParaWhatsApp(string markdown)
    {
        // Sem escape de HTML (ao contrário do Telegram): o WhatsApp trata tudo como texto puro,
        // então "<" e "&" passam sem quebrar nada.
        var texto = RemoverTabelas(markdown);

        texto = Header.Replace(texto, m => Envolver(m.Groups[1].Value, MarcaNegrito));
        texto = Negrito.Replace(texto, m => Envolver(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, MarcaNegrito));
        texto = Italico.Replace(texto, m => Envolver(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, MarcaItalico));
        texto = CodigoInline.Replace(texto, m => Envolver(m.Groups[1].Value, MarcaMono));

        return texto
            .Replace(MarcaNegrito.ToString(), "*")
            .Replace(MarcaItalico.ToString(), "_")
            .Replace(MarcaMono.ToString(), "```");
    }

    private static string Envolver(string conteudo, char marca) => $"{marca}{conteudo}{marca}";

    // WhatsApp não renderiza tabela — vira lista simples, igual no Telegram.
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
