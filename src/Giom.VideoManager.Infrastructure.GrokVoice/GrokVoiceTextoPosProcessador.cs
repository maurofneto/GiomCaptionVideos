using System.Text.RegularExpressions;

namespace Giom.VideoManager.Infrastructure.GrokVoice;

/// <summary>
/// Limpa a transcrição devolvida pela API antes de gravar (US #148): remove marcadores de áudio
/// sem fala ("Música", "[Música]", ♪) que poluiriam resumo/embedding, e colapsa a mesma frase
/// repetida em sequência (loop de decodificação típico de modelos de ASR em trechos sem fala).
/// Conservador de propósito — nunca reescreve conteúdo falado.
/// </summary>
internal static partial class GrokVoiceTextoPosProcessador
{
    /// <summary>Máximo de vezes que a MESMA frase pode aparecer em sequência.</summary>
    internal const int MaxRepeticoesSeguidas = 2;

    [GeneratedRegex(@"(?<=[.!?…])\s+")]
    private static partial Regex FimDeFrase();

    [GeneratedRegex(@"^[\s\W_]*m[uú]sica[\s\W_]*$", RegexOptions.IgnoreCase)]
    private static partial Regex SoMarcadorDeMusica();

    /// <summary>Marcadores entre colchetes/parênteses ("[Música]", "(música)") e notas musicais — podem vir colados ao texto, sem pontuação.</summary>
    [GeneratedRegex(@"[\[\(]\s*m[uú]sica\s*[\]\)]|[♪♫]+", RegexOptions.IgnoreCase)]
    private static partial Regex MarcadorInline();

    internal static string Limpar(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return string.Empty;

        texto = MarcadorInline().Replace(texto, " ");
        var frases = FimDeFrase().Split(texto.Trim());
        var saida = new List<string>(frases.Length);
        string? anterior = null;
        var repeticoes = 0;

        foreach (var bruta in frases)
        {
            var frase = bruta.Trim();
            if (!frase.Any(char.IsLetterOrDigit) || SoMarcadorDeMusica().IsMatch(frase)) continue;

            repeticoes = string.Equals(frase, anterior, StringComparison.OrdinalIgnoreCase) ? repeticoes + 1 : 1;
            anterior = frase;
            if (repeticoes > MaxRepeticoesSeguidas) continue;

            saida.Add(frase);
        }

        return string.Join(' ', saida);
    }
}
