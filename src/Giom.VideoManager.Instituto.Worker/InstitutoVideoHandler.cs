using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Application.Options;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Instituto.Worker;

/// <summary>
/// Processa aulas teológicas do Instituto Giom.
/// Transcreve com Whisper, gera resumo executivo, classifica nas 12 áreas e indexa embedding para RAG.
/// </summary>
public class InstitutoVideoHandler(
    IVideoRepository videoRepository,
    //ITranscricaoService transcricaoService,
    ICaptionService captionService,
    IResumoService resumoService,
    ICategorizacaoService categorizacaoService,
    IEmbeddingService embeddingService,
    ILogger<InstitutoVideoHandler> logger,
    IOptions<ProcessingOptions> options) : IVideoHandler
{
    private readonly ProcessingOptions _options = options.Value;

    public bool IsRelevant(Video video)
    {
        return video.Type == VideoType.Live && video.PrivacyStatus == "unlisted";
    }

    public async Task HandleAsync(Video video, CancellationToken ct = default)
    {
        var existing = await videoRepository.GetByIdAsync(video.Id, ct);
        if (existing != null)
        {
            video.Transcricao = existing.Transcricao;
            video.Resumo      = existing.Resumo;
            video.Categorias  = existing.Categorias;
            video.Chunks      = existing.Chunks;
        }

        logger.LogInformation($"Instituto — processando: {video.Title} ({video.Id}) [Existing: {existing}]");

        //if (video.Transcricao == null && _options.ShouldTranscribe)
        //{
        //    video.Transcricao = await transcricaoService.TranscreverAsync(video.Id, ct);
        //    logger.LogInformation("Instituto — transcrição obtida: {Title}", video.Title);
        //}

        try
        {
            if (video.Transcricao == null && _options.ShouldGetCaption)
            {
                video.Transcricao = await captionService.GetCaptionAsync(video, ct);
                logger.LogInformation("Instituto — legenda obtida: {Title}", video.Title);
            }

            if (video.Resumo == null && _options.ShouldGenerateSummary && video.Transcricao != null)
            {
                video.Resumo = await resumoService.GerarResumoAsync(video, ct);
                logger.LogInformation("Instituto — resumo gerado: {Title}", video.Title);
            }

            if (video.Categorias.Count == 0 && _options.ShouldCategorize && video.Resumo != null)
            {
                video.Categorias = (IList<CategoriaTeologica>)await categorizacaoService.ClassificarAsync(video, ct);
                logger.LogInformation("Instituto — categorização concluída: {Title}", video.Title);
            }
        }
        catch (Exception ex)
        {
            // Salva o progresso obtido até aqui (ex: transcrição) mesmo se resumo/categorização falharem —
            // evita perder a legenda já baixada e refazer a chamada no próximo run.
            logger.LogError(ex, "Instituto — falha ao processar {Title} ({Id}), salvando progresso parcial", video.Title, video.Id);
        }
        finally
        {
            video.ProcessedAt = DateTime.UtcNow;

            // Persiste o vídeo antes de indexar — video_chunk tem FK para video
            await videoRepository.UpsertAsync(video, ct);
        }

        // Indexa a transcrição completa em chunks — não o resumo.
        // Cada chunk (~600 tokens) recebe seu próprio embedding para RAG preciso.
        if (_options.ShouldIndexEmbedding && video.Transcricao != null && !video.Chunks.Any())
        {
            await embeddingService.IndexarAsync(video, ct);
            logger.LogInformation("Instituto — embedding indexado: {Title}", video.Title);
        }

        logger.LogInformation("Instituto — processado: {Title} ({Id})", video.Title, video.Id);
    }
}
