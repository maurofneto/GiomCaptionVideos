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

    /// <summary>
    /// Já concluiu o pipeline antes? (Bug #125/#126) — permite ao ChannelProcessor pular o
    /// vídeo SEM gastar cota do MaxVideosPerRun nele. Sem isso, ao retomar uma página que já
    /// tinha vídeos processados no início (dedup do Bug #120 cuidando de não reenfileirar),
    /// esses vídeos ainda consumiam o teto da execução só de serem reavaliados — com um teto
    /// pequeno e vários já feitos antes dos novos na mesma página, o run nunca alcançava os
    /// vídeos genuinamente novos mais adiante, travado pra sempre no mesmo ponto.
    /// </summary>
    Task<bool> JaProcessadoAsync(Video video, CancellationToken ct = default);

    Task HandleAsync(Video video, CancellationToken ct = default);
}
