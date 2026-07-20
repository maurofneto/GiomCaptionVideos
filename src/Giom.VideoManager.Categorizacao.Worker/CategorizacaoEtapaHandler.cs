using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Categorizacao.Worker;

/// <summary>
/// Etapa de Categorização, genérica (Feature #104) — hoje só o Instituto usa (Ministério não
/// categoriza), mas o worker não sabe disso: só existe fila/consumer registrado pra quem
/// publica nela (o orquestrador só publica categorização quando a topologia do canal inclui essa
/// etapa). Terminal — não publica próxima etapa de processamento, só avisa que terminou (o
/// PipelineHandler marca ProcessedAt).
/// </summary>
public class CategorizacaoEtapaHandler(
    IVideoRepository videoRepository,
    ICategorizacaoService categorizacaoService,
    [FromKeyedServices("etapa-concluida")] IFilaPublisher<EtapaConcluidaMensagem> etapaConcluidaPublisher,
    ISessionContext sessionContext,
    ITracingService tracingService,
    ILogger<CategorizacaoEtapaHandler> logger)
    : IEtapaCategorizacaoHandler
{
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
            video.Categorias = (IList<Domain.Entities.CategoriaTeologica>)await categorizacaoService.ClassificarAsync(video, ct);
            logger.LogInformation("{Canal} — categorização concluída: {Title}", video.ChannelTitle, video.Title);
        }
        else if (video.Categorias.Count == 0)
        {
            // Bug #123: mesmo padrão do ResumoEtapaHandler — sem log, o pulo por falta de resumo
            // era indistinguível de sucesso (ProcessedAt marcado normalmente).
            logger.LogWarning("{Canal} — sem resumo disponível, pulando categorização: {Title}", video.ChannelTitle, video.Title);
        }

        await videoRepository.UpsertAsync(video, ct);

        await etapaConcluidaPublisher.PublicarAsync(
            new EtapaConcluidaMensagem(video, "categorizacao", sessionContext.Atual, tracingService.CapturarContextoAtual()), ct);
    }
}
