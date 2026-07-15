using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Application.Options;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Transcricao.Worker;

/// <summary>
/// Etapa de Transcrição, genérica de verdade (Feature #104) — atende Instituto e Ministério na
/// mesma execução, sem ramificar por canal em lugar nenhum (o único secret que divergia
/// (ClaudeResumo) foi unificado; os demais já eram o mesmo valor ou nem existiam pro Ministério).
/// Só faz o trabalho da própria etapa e avisa que terminou — não decide fan-out, não marca
/// ProcessedAt. Quem decide o que vem depois é o PipelineHandler (Orquestracao.Worker), que
/// escuta EtapaConcluidaMensagem.
/// </summary>
public class TranscricaoEtapaHandler(
    IVideoRepository videoRepository,
    ICaptionService captionService,
    IOptions<ProcessingOptions> options,
    [FromKeyedServices("etapa-concluida")] IFilaPublisher<EtapaConcluidaMensagem> etapaConcluidaPublisher,
    ISessionContext sessionContext,
    ITracingService tracingService,
    ILogger<TranscricaoEtapaHandler> logger)
    : IEtapaTranscricaoHandler
{
    private readonly ProcessingOptions _options = options.Value;

    async Task IEtapaTranscricaoHandler.ProcessarAsync(Video video, CancellationToken ct)
    {
        var existing = await videoRepository.GetByIdAsync(video.Id, ct);
        if (existing != null) video.Transcricao = existing.Transcricao;

        if (video.Transcricao == null && _options.ShouldGetCaption)
        {
            video.Transcricao = await captionService.GetCaptionAsync(video, ct);
            if (video.Transcricao != null)
                logger.LogInformation("{Canal} — legenda obtida: {Title}", video.ChannelTitle, video.Title);
            else
                logger.LogWarning("{Canal} — sem legenda disponível no YouTube: {Title} ({Id})", video.ChannelTitle, video.Title, video.Id);
        }

        await videoRepository.UpsertAsync(video, ct);

        await etapaConcluidaPublisher.PublicarAsync(
            new EtapaConcluidaMensagem(video, "transcricao", sessionContext.Atual, tracingService.CapturarContextoAtual()), ct);
    }
}
