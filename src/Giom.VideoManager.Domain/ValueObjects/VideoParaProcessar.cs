using Giom.VideoManager.Domain.Entities;

namespace Giom.VideoManager.Domain.ValueObjects;

/// <summary>
/// Mensagem publicada na fila de ingestão — um vídeo novo detectado vira uma unidade de
/// trabalho independente (US #40), processada por quem consumir a fila. Só o Video (que já
/// carrega ChannelId/ChannelTitle) — IVideoHandler.HandleAsync nunca recebeu ChannelConfig
/// separado, então a mensagem não precisa de mais que isso.
/// </summary>
public record VideoParaProcessar(Video Video);
