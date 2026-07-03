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

    /// <summary>
    /// Remove timestamps, tags HTML e duplicações do conteúdo bruto.
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

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Sobrecarga para uso após o objeto Transcricao já existir (ex: serviços que processam do banco).
    /// </summary>
    public static string StripTimestamps(Transcricao transcricao)
        => StripTimestamps(transcricao.Conteudo, transcricao.Formato);
}
