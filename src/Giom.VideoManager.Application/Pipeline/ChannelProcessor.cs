using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Application.Options;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Application.Pipeline;

public class ChannelProcessor(
    IVideoSourceService videoSource,
    IProgressoRepository progressoRepository,
    IVideoHandler videoHandler,
    ILogger<ChannelProcessor> logger,
    IOptions<ProcessingOptions> options)
{
    private readonly ProcessingOptions _options = options.Value;

    public async Task RunAsync(IEnumerable<ChannelConfig> channels, CancellationToken ct = default)
    {
        foreach (var config in channels)
        {
            logger.LogInformation("Iniciando canal: {Name}", config.Name);
            try
            {
                await ProcessChannelAsync(config, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro ao processar canal {Name}", config.Name);
            }
        }

        logger.LogInformation("Processamento concluído.");
    }

    private async Task ProcessChannelAsync(ChannelConfig config, CancellationToken ct)
    {
        var playlistId = await videoSource.GetUploadPlaylistIdAsync(config, ct);
        if (string.IsNullOrEmpty(playlistId))
        {
            logger.LogWarning("Playlist não encontrada para canal {Name}", config.Name);
            return;
        }

        var channelKey       = config.ChannelId ?? "mine";
        var pageToken        = await progressoRepository.GetLastPageTokenAsync(channelKey, playlistId, ct);
        var lastProcessedDate = await progressoRepository.GetLastProcessedDateAsync(channelKey, playlistId, ct);

        bool applyDateFilter = pageToken == null;

        // Congela o corte de data usado só pra decidir o que pular — a playlist do YouTube
        // vem do mais novo pro mais antigo, então assim que o primeiro vídeo (o mais novo)
        // era processado, lastProcessedDate avançava pra aquela data e passava a filtrar
        // TODO o resto da mesma execução como "antigo demais", limitando cada run a 1 vídeo
        // processado, mesmo com MaxVideosPerRun bem maior e o progresso zerado.
        var filtroDataBase = lastProcessedDate;

        if (pageToken != null)
            logger.LogInformation("Canal {Name} — retomando da página salva (filtro de data desativado)", config.Name);
        else if (filtroDataBase.HasValue)
            logger.LogInformation("Canal {Name} — iniciando do topo, ignorando vídeos antes de {Date:d}", config.Name, filtroDataBase);

        var processedIds = new HashSet<string>();

        while (processedIds.Count < _options.MaxVideosPerRun && !ct.IsCancellationRequested)
        {
            logger.LogInformation($"Canal {config.Name} — processando página (já processados: {processedIds.Count})", config.Name);
            var page = await videoSource.GetPlaylistPageAsync(playlistId, Math.Min(50, _options.MaxVideosPerRun), pageToken, ct);

            if (page.Videos.Count == 0) break;

            foreach (var video in page.Videos)
            {
                if (processedIds.Count >= _options.MaxVideosPerRun || ct.IsCancellationRequested) break;

                if (applyDateFilter && video.UploadedAt <= (filtroDataBase ?? DateTime.MinValue)) continue;

                if (!videoHandler.IsRelevant(video)) continue;

                if (!processedIds.Add(video.Id)) continue;

                logger.LogInformation("Canal {Name} — processando vídeo {Id} — {Title}", config.Name, video.Id, video.Title);

                try
                {
                    await videoHandler.HandleAsync(video, ct);

                    if ((video.UploadedAt ?? DateTime.MinValue) > (lastProcessedDate ?? DateTime.MinValue))
                        lastProcessedDate = video.UploadedAt;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Erro ao processar vídeo {Id} — {Title}", video.Id, video.Title);
                }

                await Task.Delay(_options.DelayBetweenVideosMs, ct);
            }

            pageToken = page.NextPageToken;
            await progressoRepository.SaveProgressoAsync(channelKey, playlistId, pageToken, lastProcessedDate, ct);

            logger.LogInformation("Canal {Name} — página processada (próxima página: {NextPageToken})", config.Name, pageToken ?? "fim");

            if (string.IsNullOrEmpty(pageToken))
            {
                // Fim da playlist — sem isso, o while continuava rodando (processedIds.Count
                // ainda < MaxVideosPerRun) e reiniciava a paginação do zero pra sempre, num
                // loop infinito que nunca deixava o processo terminar.
                logger.LogInformation("Canal {Name} — playlist percorrida até o fim ({Count} processados)", config.Name, processedIds.Count);
                break;
            }

            await Task.Delay(_options.DelayBetweenPagesMs, ct);
        }
    }
}
