using Giom.VideoManager.Domain.Entities;

namespace Giom.VideoManager.Application.Handlers;

/// <summary>
/// Define o que fazer com cada vídeo após ser coletado pelo ChannelProcessor. Quem ChannelProcessor
/// injeta aqui nem sempre é quem processa de verdade — pode ser FilaVideoHandler (US #39/#40),
/// que enfileira em vez de processar, delegando pra IEtapaTranscricaoHandler via EtapaQueueConsumer
/// (Feature #33 — Transcrição é só mais uma etapa, não um caso especial).
/// </summary>
public interface IVideoHandler
{
    /// <summary>Decide se o vídeo deve ser processado (filtro de relevância).</summary>
    bool IsRelevant(Video video);

    Task HandleAsync(Video video, CancellationToken ct = default);
}
