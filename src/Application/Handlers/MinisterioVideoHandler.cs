using Application.Options;
using Domain.Entities;
using Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Handlers;

/// <summary>
/// Processa vídeos do canal do Ministério Giom.
/// Baixa legenda do YouTube e gera resumo do culto (orador, assunto, registros).
/// </summary>
public class MinisterioVideoHandler(
    IVideoRepository videoRepository,
    ICaptionService captionService,
    IResumoService resumoService,
    ILogger<MinisterioVideoHandler> logger,
    IOptions<ProcessingOptions> options) : IVideoHandler
{
    private readonly ProcessingOptions _options = options.Value;

    public bool IsRelevant(Video video)
    {
        var title = video.Title.ToUpperInvariant();
        return title.Contains("CULTO") ||
               title.Contains("MINISTÉRIO") ||
               title.StartsWith("GIOM");
    }

    public async Task HandleAsync(Video video, CancellationToken ct = default)
    {
        var existing = await videoRepository.GetByIdAsync(video.Id, ct);
        if (existing != null)
        {
            video.Transcricao = existing.Transcricao;
            video.Resumo      = existing.Resumo;
            video.Chunks      = existing.Chunks;
        }

        try
        {
            if (video.Transcricao == null && _options.ShouldGetCaption)
            {
                video.Transcricao = await captionService.GetCaptionAsync(video, ct);
            }

            if (video.Resumo == null && _options.ShouldGenerateSummary && video.Transcricao != null)
            {
                video.Resumo = await resumoService.GerarResumoAsync(video, ct);
            }
        }
        catch (Exception ex)
        {
            // Salva o progresso obtido até aqui (ex: transcrição) mesmo se o resumo falhar —
            // evita perder a legenda já baixada e refazer a chamada no próximo run.
            logger.LogError(ex, "Ministério — falha ao processar {Title} ({Id}), salvando progresso parcial", video.Title, video.Id);
        }
        finally
        {
            video.ProcessedAt = DateTime.UtcNow;
            await videoRepository.UpsertAsync(video, ct);
        }

        logger.LogInformation("Ministério — processado: {Title} ({Id})", video.Title, video.Id);
    }
}
