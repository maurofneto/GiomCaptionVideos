using Giom.VideoManager.Domain.Entities;
using System.Text.RegularExpressions;

namespace Giom.VideoManager.Infrastructure;

/// <summary>
/// Remove timestamps e tags do conteúdo de transcrições.
/// As transcrições são salvas com o conteúdo original intacto (Conteudo)
/// e com o texto limpo pronto para IA/embeddings (ConteudoLimpo).
/// </summary>
public static class TranscricaoHelper
{
    // SBV: 0:01:06.155,0:01:08.175
    private static readonly Regex SbvTimestamp = new(
        @"^\d+:\d{2}:\d{2}\.\d{3},\d+:\d{2}:\d{2}\.\d{3}$",
        RegexOptions.Compiled);

    // VTT cue: 00:01:06.155 --> 00:01:08.175 (com posição opcional)
    private static readonly Regex VttTimestamp = new(
        @"^\d{2}:\d{2}:\d{2}\.\d{3}\s*-->\s*\d{2}:\d{2}:\d{2}\.\d{3}",
        RegexOptions.Compiled);

    // Interjeições litúrgicas repetidas em sequência (ex: "Aleluia! Glória a Deus! Aleluia!
    // Deus! Glória a Deus! Glória a Deus!") não carregam conteúdo teológico e diluem o
    // embedding do chunk — 3+ ocorrências seguidas colapsam pra uma única.
    private static readonly Regex RepeticaoLiturgica = new(
        @"(?:\b(?:Aleluia|Am[ée]m|Gl[óo]ria a Deus|Gl[óo]ria)\b[!.,]?\s*){3,}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Remove timestamps, tags HTML, duplicações e ruído litúrgico repetitivo do conteúdo bruto.
    /// Detecta o formato via parâmetro (sbv, vtt, srt).
    /// </summary>
    public static string StripTimestamps(string conteudo, string formato)
    {
        var lines = conteudo.Split('\n', StringSplitOptions.TrimEntries);
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))  continue;
            if (line.StartsWith("WEBVTT"))         continue;
            if (line.StartsWith("NOTE"))           continue;
            if (VttTimestamp.IsMatch(line))        continue;
            if (SbvTimestamp.IsMatch(line))        continue;

            var clean = Regex.Replace(line, @"<[^>]+>", "").Trim();
            if (string.IsNullOrWhiteSpace(clean))  continue;

            if (seen.Add(clean))
                parts.Add(clean);
        }

        var texto = string.Join(" ", parts);
        return RepeticaoLiturgica.Replace(texto, match => Regex.Match(match.Value, @"\b\w+\b").Value + ". ");
    }

    /// <summary>
    /// Sobrecarga para uso após o objeto Transcricao já existir (ex: serviços que processam do banco).
    /// </summary>
    public static string StripTimestamps(Transcricao transcricao)
        => StripTimestamps(transcricao.Conteudo, transcricao.Formato);
}
