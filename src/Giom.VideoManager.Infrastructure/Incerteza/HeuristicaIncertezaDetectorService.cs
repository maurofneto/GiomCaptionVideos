using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Infrastructure.Incerteza;

/// <summary>
/// Heurística por palavras-chave — não é 100% precisa (o texto varia a cada geração), mas
/// correlaciona bem com as instruções da persona (agent.md) pra admitir incerteza e mencionar
/// o Dr. Wagner. Serve como proxy de "buraco de conteúdo", não como detecção exata.
/// </summary>
public class HeuristicaIncertezaDetectorService : IIncertezaDetectorService
{
    private static readonly string[] SinaisIncerteza =
    [
        "não sei", "não tenho certeza", "não consigo confirmar", "não consigo afirmar",
        "não vi registrad", "não tenho essa informação", "não encontrei isso nas aulas",
        "vale a pena perguntar", "procure o dr. wagner", "procurar o dr. wagner"
    ];

    public bool ContemIncerteza(string texto) =>
        SinaisIncerteza.Any(sinal => texto.Contains(sinal, StringComparison.OrdinalIgnoreCase));
}
