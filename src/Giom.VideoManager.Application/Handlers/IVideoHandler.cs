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

/// <summary>
/// Filtro de relevância de um vídeo — implementado por cada Worker (MinisterioVideoHandler:
/// título contém "CULTO"/"MINISTÉRIO"; InstitutoVideoHandler: Live + unlisted). Existe separado de
/// IVideoHandler pelo mesmo motivo de ITelemetriaSink vs ITelemetriaService (ver
/// ITelemetriaService.cs): ChannelProcessor fala com "quem enfileira" (FilaVideoHandler, registrado
/// como IVideoHandler), que só usa este papel pra IsRelevant — sem essa distinção, os dois
/// colidiriam na mesma chave de DI (só o último registrado como IVideoHandler "ganharia"). O
/// processamento real de cada etapa vive em InstitutoPipelineHandler/MinisterioPipelineHandler
/// (Feature #33), não em HandleAsync desta interface.
/// </summary>
public interface IVideoProcessador : IVideoHandler;
