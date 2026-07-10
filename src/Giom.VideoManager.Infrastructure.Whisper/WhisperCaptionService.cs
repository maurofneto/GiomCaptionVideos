using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Infrastructure.Whisper;

/// <summary>
/// Adapta ITranscricaoService (baixa áudio + Whisper, videoId como string) pra ICaptionService
/// (Video → Transcricao?), pra poder entrar na cadeia de fallback de Transcrição (Feature #101)
/// junto com YouTubeCaption/YtDlpCaption — último recurso, mais caro/lento (download de áudio +
/// transcrição local), só acionado quando os providers de legenda não encontram nada.
/// </summary>
public class WhisperCaptionService(ITranscricaoService transcricaoService) : ICaptionService
{
    public async Task<Transcricao?> GetCaptionAsync(Video video, CancellationToken ct = default)
    {
        var transcricao = await transcricaoService.TranscreverAsync(video.Id, ct);
        if (transcricao == null) return null;

        // Saída do Whisper já é texto limpo, sem timestamps/tags — ConteudoLimpo == Conteudo.
        transcricao.ConteudoLimpo = transcricao.Conteudo;
        return transcricao;
    }
}
