using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Application.Options;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Ministerio.Worker;

/// <summary>
/// Orquestrador orientado a eventos do Ministério (Feature #33, US #41/#42/#43) — reaproveita os
/// mesmos serviços que <see cref="MinisterioVideoHandler"/> já usa (esta classe não é tocada,
/// continua servindo só de IVideoProcessador/IsRelevant pro FilaVideoHandler). Ministério só tem
/// 2 etapas (Transcrição + Resumo) — não implementa IEtapaCategorizacaoHandler/
/// IEtapaEmbeddingHandler, não existem essas filas/consumers pro Ministério (ISP).
/// </summary>
public class MinisterioPipelineHandler(
    IVideoRepository videoRepository,
    ICaptionService captionService,
    IResumoService resumoService,
    IOptions<ProcessingOptions> options,
    [FromKeyedServices("resumo")] IFilaPublisher<VideoEtapaMensagem> resumoPublisher,
    ISessionContext sessionContext,
    ITracingService tracingService,
    ILogger<MinisterioPipelineHandler> logger)
    : IEtapaTranscricaoHandler, IEtapaResumoHandler
{
    private readonly ProcessingOptions _options = options.Value;

    async Task IEtapaTranscricaoHandler.ProcessarAsync(Video video, CancellationToken ct)
    {
        var existing = await videoRepository.GetByIdAsync(video.Id, ct);
        if (existing != null) video.Transcricao = existing.Transcricao;

        if (video.Transcricao == null && _options.ShouldGetCaption)
        {
            video.Transcricao = await captionService.GetCaptionAsync(video, ct);
        }

        await videoRepository.UpsertAsync(video, ct);

        if (_options.ShouldGenerateSummary)
            await resumoPublisher.PublicarAsync(new VideoEtapaMensagem(video, sessionContext.Atual, tracingService.CapturarContextoAtual()), ct);
    }

    async Task IEtapaResumoHandler.ProcessarAsync(Video video, CancellationToken ct)
    {
        var existing = await videoRepository.GetByIdAsync(video.Id, ct);
        if (existing != null)
        {
            video.Transcricao ??= existing.Transcricao;
            video.Resumo = existing.Resumo;
        }

        if (video.Resumo == null && video.Transcricao != null)
        {
            video.Resumo = await resumoService.GerarResumoAsync(video, ct);
        }

        video.ProcessedAt = DateTime.UtcNow;
        await videoRepository.UpsertAsync(video, ct);

        logger.LogInformation("Ministério — processado: {Title} ({Id})", video.Title, video.Id);
        // Terminal — não publica próxima etapa.
    }
}
