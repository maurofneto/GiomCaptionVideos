namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Avalia um conteúdo (pergunta ou resposta) contra uma instrução em linguagem natural,
/// usando IA em vez de regra fixa (heurística de palavras, blocklist, etc.). Genérico o
/// bastante pra servir de guardrail plugável (ver GuardRailIA na Infrastructure) sem acoplar
/// a decisão a nenhum provider de IA específico — hoje só existe uma implementação (Claude),
/// mas trocar por outro modelo/vendor não muda quem consome esta interface.
/// </summary>
public interface IAvaliadorIAService
{
    Task<AvaliacaoIA> AvaliarAsync(string instrucao, string conteudo, CancellationToken ct = default);
}

/// <summary>Motivo só é preenchido quando Aprovado=false (ou quando o avaliador decidiu explicar mesmo aprovando).</summary>
public record AvaliacaoIA(bool Aprovado, string? Motivo = null);
