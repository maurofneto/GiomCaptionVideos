namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Rastreia, por vídeo, quais etapas terminais de uma topologia com fan-out já foram concluídas
/// (bug #111 — ver PipelineHandler). Uma única linha por (videoId, etapa); idempotente.
/// </summary>
public interface IVideoEtapaTerminalRepository
{
    /// <summary>Registra a conclusão de uma etapa terminal e devolve o conjunto completo de etapas terminais já concluídas pra esse vídeo (incluindo esta).</summary>
    Task<IReadOnlySet<string>> RegistrarConclusaoAsync(string videoId, string etapa, CancellationToken ct = default);
}
