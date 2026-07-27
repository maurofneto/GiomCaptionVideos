using System.Text.RegularExpressions;

namespace Giom.VideoManager.Infrastructure.Observabilidade;

/// <summary>
/// Implementação genérica de ISentryDataScrubber pra segredos que seguem um formato
/// reconhecível por regex (Bug #128) — evita reescrever "aplica regex e substitui" a cada novo
/// segredo; cada adapter só declara o padrão, sem precisar de uma classe própria.
/// </summary>
public sealed class RegexSentryDataScrubber(string padrao, string substituto = "***") : ISentryDataScrubber
{
    private readonly Regex _regex = new(padrao, RegexOptions.Compiled);

    public string Redigir(string texto) => _regex.Replace(texto, substituto);
}
