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
/// Orquestrador orientado a eventos do Ministério (Feature #33, US #41/#42/#43) — único handler
/// ativo do canal. <see cref="MinisterioVideoHandler"/> fica no repositório só de histórico/
/// rollback (não é tocado, não é registrado em nenhum DI, não roda mais) — IVideoProcessador
/// só existia pra distinguir "quem enfileira" de "quem processa de verdade", sem sentido agora
/// que os dois são a mesma classe, então IsRelevant vive aqui direto.
/// Dono da própria topologia — implementa IVideoHandler diretamente, sem depender de um
/// FilaVideoHandler genérico compartilhado (ver InstitutoPipelineHandler pro raciocínio completo).
/// Ministério só tem 2 etapas (Transcrição + Resumo) — não implementa
/// IEtapaCategorizacaoHandler/IEtapaEmbeddingHandler, não existem essas filas/consumers pro
/// Ministério (ISP).
/// </summary>
public class MinisterioPipelineHandler(
    IVideoRepository videoRepository,
    ICaptionService captionService,
    IResumoService resumoService,
    IOptions<ProcessingOptions> options,
    [FromKeyedServices("transcricao")] IFilaPublisher<VideoEtapaMensagem> transcricaoPublisher,
    [FromKeyedServices("resumo")] IFilaPublisher<VideoEtapaMensagem> resumoPublisher,
    ISessionContext sessionContext,
    ITracingService tracingService,
    ILogger<MinisterioPipelineHandler> logger)
    : IVideoHandler, IEtapaTranscricaoHandler, IEtapaResumoHandler
{
    private readonly ProcessingOptions _options = options.Value;

    /// <summary>Mesmo critério de MinisterioVideoHandler.IsRelevant — título indica culto/ministério.</summary>
    public bool IsRelevant(Video video)
    {
        var title = video.Title.ToUpperInvariant();
        return title.Contains("CULTO") || title.Contains("MINISTÉRIO") || title.StartsWith("GIOM");
    }

    public Task HandleAsync(Video video, CancellationToken ct = default) =>
        transcricaoPublisher.PublicarAsync(new VideoEtapaMensagem(video, sessionContext.Atual, tracingService.CapturarContextoAtual()), ct);

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
