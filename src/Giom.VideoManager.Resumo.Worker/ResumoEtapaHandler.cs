using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Resumo.Worker;

/// <summary>
/// Etapa de Resumo, genérica (Feature #104) — atende Instituto e Ministério na mesma execução.
/// A chave Claude usada aqui é a única credencial unificada entre os dois canais (antes divergia
/// por canal; Grok/OpenAI já eram o mesmo valor duplicado). Só faz o trabalho da etapa e avisa
/// que terminou — quem decide se o próximo passo é Categorização (Instituto) ou nada
/// (Ministério, terminal) é o PipelineHandler (Orquestracao.Worker).
/// </summary>
public class ResumoEtapaHandler(
    IVideoRepository videoRepository,
    IResumoService resumoService,
    [FromKeyedServices("etapa-concluida")] IFilaPublisher<EtapaConcluidaMensagem> etapaConcluidaPublisher,
    ISessionContext sessionContext,
    ITracingService tracingService,
    ILogger<ResumoEtapaHandler> logger)
    : IEtapaResumoHandler
{
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
            logger.LogInformation("{Canal} — resumo gerado: {Title}", video.ChannelTitle, video.Title);
        }

        await videoRepository.UpsertAsync(video, ct);

        await etapaConcluidaPublisher.PublicarAsync(
            new EtapaConcluidaMensagem(video, "resumo", sessionContext.Atual, tracingService.CapturarContextoAtual()), ct);
    }
}
