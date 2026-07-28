using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Infrastructure.Whisper;

/// <summary>
/// Adapta ITranscricaoService (baixa áudio + transcreve via API, videoId como string) pra
/// ICaptionService (Video → Transcricao?), pra poder entrar na cadeia de fallback de Transcrição
/// (Feature #101) junto com YouTubeCaption/YtDlpCaption — último recurso, mais caro/lento
/// (download de áudio + chamada de API), só acionado quando os providers de legenda não acham
/// nada. TranscreverAsync (Bug #134) lança <see cref="Giom.VideoManager.Domain.Exceptions.NenhumResultadoEncontradoException"/>
/// em vez de devolver null quando não consegue transcrever — nunca retorna null de verdade, só
/// propaga a exceção pro próximo (inexistente) elo da cadeia.
/// </summary>
public class WhisperCaptionService(ITranscricaoService transcricaoService) : ICaptionService
{
    public Task<Transcricao?> GetCaptionAsync(Video video, CancellationToken ct = default)
        => transcricaoService.TranscreverAsync(video.Id, ct);
}
