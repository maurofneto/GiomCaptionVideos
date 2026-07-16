using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Embedding.Worker;

/// <summary>
/// Etapa de Embedding, genérica (Feature #104) — hoje só o Instituto usa. Terminal — não publica
/// próxima etapa de processamento, só avisa que terminou (o PipelineHandler marca ProcessedAt).
/// </summary>
public class EmbeddingEtapaHandler(
    IVideoRepository videoRepository,
    IVideoIndexingService embeddingIndexingService,
    [FromKeyedServices("etapa-concluida")] IFilaPublisher<EtapaConcluidaMensagem> etapaConcluidaPublisher,
    ISessionContext sessionContext,
    ITracingService tracingService,
    ILogger<EmbeddingEtapaHandler> logger)
    : IEtapaEmbeddingHandler
{
    async Task IEtapaEmbeddingHandler.ProcessarAsync(Video video, CancellationToken ct)
    {
        var existing = await videoRepository.GetByIdAsync(video.Id, ct);
        if (existing != null)
        {
            video.Transcricao ??= existing.Transcricao;
            video.Chunks = existing.Chunks;
        }

        // Não filtra por video.Chunks.Any() aqui: um vídeo pode já ter chunk de UM provedor mas
        // não de outro (pipeline multi-provedor, Feature #115/Bug #121) — quem decide por
        // provedor é o próprio IVideoIndexingService (cada provider checa sua própria Origem).
        if (video.Transcricao != null)
        {
            await embeddingIndexingService.IndexarAsync(video, ct);
            logger.LogInformation("{Canal} — embedding indexado: {Title}", video.ChannelTitle, video.Title);
        }

        await etapaConcluidaPublisher.PublicarAsync(
            new EtapaConcluidaMensagem(video, "embedding", sessionContext.Atual, tracingService.CapturarContextoAtual()), ct);
    }
}
