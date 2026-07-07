using Giom.VideoManager.Domain.Entities;

namespace Giom.VideoManager.Domain.ValueObjects;

/// <summary>
/// Mensagem publicada na fila de ingestão — um vídeo novo detectado vira uma unidade de
/// trabalho independente (US #40), processada por quem consumir a fila.
/// </summary>
public record VideoParaProcessar(ChannelConfig Canal, Video Video);
