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
/// Orquestrador orientado a eventos do Instituto (Feature #33, US #41/#42/#43) — único handler
/// ativo do canal. <see cref="InstitutoVideoHandler"/> fica no repositório só de histórico/
/// rollback (não é tocado, não é registrado em nenhum DI, não roda mais) — o papel de
/// IVideoProcessador que existia só pra distinguir "quem enfileira" de "quem processa de verdade"
/// não faz mais sentido agora que os dois são a mesma classe, então IsRelevant vive aqui direto,
/// sem indireção.
/// Cada canal é DONO da própria topologia — não existe mais um FilaVideoHandler genérico
/// decidindo por todo mundo "descoberta sempre publica na fila X": este orquestrador implementa
/// IVideoHandler diretamente e decide sozinho o que fazer com um vídeo recém-descoberto (aqui:
/// publica pra própria etapa de Transcrição). Um canal futuro que não precise de Transcrição como
/// primeira etapa (ex: já recebe o conteúdo pronto, ou quer pular direto pro Resumo) implementa
/// isso do jeito que quiser, sem tocar nesta classe nem em nenhuma abstração compartilhada.
/// Dependências reais entre etapas (não é uma corrente linear): Resumo depende de Transcrição;
/// Categorização depende de Resumo; Embedding depende só de Transcrição (indexa a transcrição
/// completa, não o resumo) — por isso Transcrição faz FAN-OUT pra Resumo E Embedding em paralelo,
/// não uma corrente única.
/// Cada método reaplica a mesma sessão/traceparent capturados (via ISessionContext.Atual/
/// ITracingService.CapturarContextoAtual) na mensagem da PRÓXIMA etapa — sem isso a árvore de
/// spans e as propriedades do Helicone se perderiam a cada hop.
/// </summary>
public class InstitutoPipelineHandler(
    IVideoRepository videoRepository,
    ICaptionService captionService,
    IResumoService resumoService,
    ICategorizacaoService categorizacaoService,
    IEmbeddingService embeddingService,
    IOptions<ProcessingOptions> options,
    [FromKeyedServices("transcricao")] IFilaPublisher<VideoEtapaMensagem> transcricaoPublisher,
    [FromKeyedServices("resumo")] IFilaPublisher<VideoEtapaMensagem> resumoPublisher,
    [FromKeyedServices("categorizacao")] IFilaPublisher<VideoEtapaMensagem> categorizacaoPublisher,
    [FromKeyedServices("embedding")] IFilaPublisher<VideoEtapaMensagem> embeddingPublisher,
    ISessionContext sessionContext,
    ITracingService tracingService,
    ILogger<InstitutoPipelineHandler> logger)
    : IVideoHandler, IEtapaTranscricaoHandler, IEtapaResumoHandler, IEtapaCategorizacaoHandler, IEtapaEmbeddingHandler
{
    private readonly ProcessingOptions _options = options.Value;

    /// <summary>Mesmo critério de InstitutoVideoHandler.IsRelevant — vídeos ao vivo não listados.</summary>
    public bool IsRelevant(Video video) => video.Type == VideoType.Live && video.PrivacyStatus == "unlisted";

    public Task HandleAsync(Video video, CancellationToken ct = default) =>
        PublicarAsync(transcricaoPublisher, video, ct);

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
