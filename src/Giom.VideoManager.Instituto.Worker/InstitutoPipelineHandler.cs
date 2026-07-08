using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Application.Options;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Enums;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Instituto.Worker;

/// <summary>
/// Orquestrador orientado a eventos do Instituto (Feature #33, US #41/#42/#43) — reaproveita os
/// mesmos serviços de domínio que <see cref="InstitutoVideoHandler"/> já usa (esta classe não é
/// tocada, continua servindo só de IVideoProcessador/IsRelevant pro FilaVideoHandler), só que cada
/// etapa vira sua própria mensagem/fila em vez de tudo numa chamada só.
/// Dependências reais entre etapas (não é uma corrente linear): Resumo depende de Transcrição;
/// Categorização depende de Resumo; Embedding depende só de Transcrição (indexa a transcrição
/// completa, não o resumo) — por isso Transcrição faz FAN-OUT pra Resumo E Embedding em paralelo,
/// não uma corrente única.
/// Cada ProcessarAsync reaplica a mesma sessão/traceparent capturados pelo consumer (via
/// ISessionContext.Atual/ITracingService.CapturarContextoAtual) na mensagem da PRÓXIMA etapa —
/// sem isso a árvore de spans e as propriedades do Helicone se perderiam a cada hop.
/// </summary>
public class InstitutoPipelineHandler(
    IVideoRepository videoRepository,
    ICaptionService captionService,
    IResumoService resumoService,
    ICategorizacaoService categorizacaoService,
    IEmbeddingService embeddingService,
    IOptions<ProcessingOptions> options,
    [FromKeyedServices("resumo")] IFilaPublisher<VideoEtapaMensagem> resumoPublisher,
    [FromKeyedServices("categorizacao")] IFilaPublisher<VideoEtapaMensagem> categorizacaoPublisher,
    [FromKeyedServices("embedding")] IFilaPublisher<VideoEtapaMensagem> embeddingPublisher,
    ISessionContext sessionContext,
    ITracingService tracingService,
    ILogger<InstitutoPipelineHandler> logger)
    : IEtapaTranscricaoHandler, IEtapaResumoHandler, IEtapaCategorizacaoHandler, IEtapaEmbeddingHandler
{
    private readonly ProcessingOptions _options = options.Value;

    async Task IEtapaTranscricaoHandler.ProcessarAsync(Video video, CancellationToken ct)
    {
        var existing = await videoRepository.GetByIdAsync(video.Id, ct);
        if (existing != null) video.Transcricao = existing.Transcricao;

        if (video.Transcricao == null && _options.ShouldGetCaption)
        {
            video.Transcricao = await captionService.GetCaptionAsync(video, ct);
            logger.LogInformation("Instituto — legenda obtida: {Title}", video.Title);
        }

        await videoRepository.UpsertAsync(video, ct);

        // Fan-out: Resumo e Embedding dependem só de Transcrição, não um do outro — publica os
        // dois de forma independente quando habilitados.
        if (_options.ShouldGenerateSummary) await PublicarAsync(resumoPublisher, video, ct);
        if (_options.ShouldIndexEmbedding) await PublicarAsync(embeddingPublisher, video, ct);
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
            logger.LogInformation("Instituto — resumo gerado: {Title}", video.Title);
        }

        await videoRepository.UpsertAsync(video, ct);

        if (_options.ShouldCategorize) await PublicarAsync(categorizacaoPublisher, video, ct);
    }

    async Task IEtapaCategorizacaoHandler.ProcessarAsync(Video video, CancellationToken ct)
    {
        var existing = await videoRepository.GetByIdAsync(video.Id, ct);
        if (existing != null)
        {
            video.Resumo ??= existing.Resumo;
            video.Categorias = existing.Categorias;
        }

        if (video.Categorias.Count == 0 && video.Resumo != null)
        {
            video.Categorias = (IList<CategoriaTeologica>)await categorizacaoService.ClassificarAsync(video, ct);
            logger.LogInformation("Instituto — categorização concluída: {Title}", video.Title);
        }

        // ProcessedAt marcado aqui e no Embedding (os 2 ramos terminais do fan-out) — cada um
        // marca "última etapa que me tocou", sem tentar coordenar qual dos dois terminou por
        // último (não é uma corrente linear, então nenhum dos dois sabe do outro).
        video.ProcessedAt = DateTime.UtcNow;
        await videoRepository.UpsertAsync(video, ct);
        // Terminal — não publica próxima etapa.
    }

    async Task IEtapaEmbeddingHandler.ProcessarAsync(Video video, CancellationToken ct)
    {
        var existing = await videoRepository.GetByIdAsync(video.Id, ct);
        if (existing != null)
        {
            video.Transcricao ??= existing.Transcricao;
            video.Chunks = existing.Chunks;
        }

        if (video.Transcricao != null && !video.Chunks.Any())
        {
            await embeddingService.IndexarAsync(video, ct);
            logger.LogInformation("Instituto — embedding indexado: {Title}", video.Title);
        }
        // Terminal — não publica próxima etapa.
    }

    private Task PublicarAsync(IFilaPublisher<VideoEtapaMensagem> publisher, Video video, CancellationToken ct) =>
        publisher.PublicarAsync(new VideoEtapaMensagem(video, sessionContext.Atual, tracingService.CapturarContextoAtual()), ct);
}
