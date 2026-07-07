namespace Giom.VideoManager.Application.Options;

/// <summary>
/// Controla quanto do histórico de conversa entra no prompt de cada pergunta. O modelo
/// (Sonnet 5, 1M tokens) aguenta uma conversa inteira sem problema, mas cada turno enviado de
/// novo é custo repetido — por isso o limite aqui é bem menor que a janela do modelo,
/// protegendo o gasto por interação, não só evitando estourar o contexto.
/// </summary>
public class HistoricoOptions
{
    /// <summary>Estimativa por caracteres (não por tokens reais) — simples e suficiente pra decidir quantos turnos manter.</summary>
    public int LimiteCaracteres { get; init; } = 12_000;
}
