using Giom.VideoManager.Domain.Entities;

namespace Giom.VideoManager.Application.Handlers;

/// <summary>
/// Define o que fazer com cada vídeo após ser coletado pelo ChannelProcessor. Quem ChannelProcessor
/// injeta aqui nem sempre é quem processa de verdade — pode ser FilaVideoHandler (US #39/#40),
/// que enfileira em vez de processar, delegando pra IVideoProcessador via VideoQueueConsumer.
/// </summary>
public interface IVideoHandler
{
    /// <summary>Decide se o vídeo deve ser processado (filtro de relevância).</summary>
    bool IsRelevant(Video video);

    Task HandleAsync(Video video, CancellationToken ct = default);
}

/// <summary>
/// Processamento real de um vídeo — implementado por cada Worker (MinisterioVideoHandler:
/// legenda + resumo do culto; InstitutoVideoHandler: transcrição + resumo + categoria +
/// embedding). Existe separado de IVideoHandler pelo mesmo motivo de ITelemetriaSink vs
/// ITelemetriaService (ver ITelemetriaService.cs): ChannelProcessor fala com "quem enfileira"
/// (FilaVideoHandler, registrado como IVideoHandler), e VideoQueueConsumer fala com "quem
/// processa de verdade" (este papel) — sem essa distinção, os dois colidiriam na mesma chave de
/// DI (só o último registrado como IVideoHandler "ganharia").
/// </summary>
public interface IVideoProcessador : IVideoHandler;
