using Core;
using Core.Dto;
using Core.Models;
using Core.Repositories;
using Google.Api.Gax.ResourceNames;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using Google.Cloud.Translate.V3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Serilog;
using System.Xml;

namespace ConsoleApp2;

public class ChannelProcessor(
    YouTubeService youtubeService,
    TranslationServiceClient translationClient,
    ResumoVideoRepository resumoVideoRepository,
    ProgressoRepository progressoRepository,
    IYouTubeApiService youtubeApiService,
    ILogger<ChannelProcessor> logger,
    IOptions<RuntimeOptions> runtimeOptions,
    IConfiguration configuration)
{
    private readonly RuntimeOptions _runtimeOptions = runtimeOptions.Value;
    private static string deviceId = Guid.NewGuid().ToString();
    private string ProjectId => configuration["Google:ProjectId"] ?? throw new InvalidOperationException("Google:ProjectId não configurado");

    public async Task RunAsync()
    {
        try
        {
            logger.LogInformation("Iniciando processamento - {DateTime}", DateTime.Now);

            var channelsToProcess = new[]
            {
                new ChannelConfig("UC2nGdI8CHMWduorPhrYow0A", null, "Meu Canal", Mine: false),
                new ChannelConfig("UCyHMyZH5oon8jW7y_yMRstQ", null, "Ministério Giom"), // Uploads
                //new ChannelConfig("UCyyXFx2xHdzfavx6H6Q8zJw", "PLAuDF8sEOh8d7SjLBYzdqggn6vEbNibqB", "Instituto Giom"),
            };

            foreach (var config in channelsToProcess)
            {
                logger.LogInformation("Processando canal/playlist: {Name}", config.Name);
                try
                {
                    await ProcessChannelAsync(config);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Erro ao processar canal {Name}", config.Name);
                }
            }

            Console.WriteLine("\nProcessamento concluído.");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Erro fatal na execução principal");
            return;
        }
    }

    private async Task ProcessChannelAsync(ChannelConfig config)
    {
        string playlistId = await GetPlaylistIdAsync(config);
        if (string.IsNullOrEmpty(playlistId)) return;

        string channelKey = config.ChannelId ?? "mine";
        string lastPageToken = await progressoRepository.GetLastPageTokenAsync(channelKey, playlistId);
        DateTime? lastProcessedDate = await progressoRepository.GetLastProcessedDateAsync(channelKey, playlistId);

        int processedCount = 0;
        DateTime maxUploadedThisRun = DateTime.MinValue;

        var request = youtubeService.PlaylistItems.List("contentDetails,snippet,status");
        request.PlaylistId = playlistId;
        request.MaxResults = Math.Min(50, _runtimeOptions.MaxVideosPerRun);
        if (!string.IsNullOrEmpty(lastPageToken))
            request.PageToken = lastPageToken;

        while (processedCount < _runtimeOptions.MaxVideosPerRun)
        {
            var response = await youtubeApiService.ListPlaylistItemsAsync(request);
            if (response.Items.Count == 0) break;

            var videoIds = response.Items
                .Select(i => i.Snippet?.ResourceId?.VideoId)
                .Where(id => !string.IsNullOrEmpty(id))
                .ToList();

            if (videoIds.Count == 0) break;

            var videosDetails = await GetVideosDetailsAsync(videoIds);

            foreach (var playlistItem in response.Items)
            {
                if (processedCount >= _runtimeOptions.MaxVideosPerRun) break;

                var video = MapToVideoModel(playlistItem, videosDetails);
                if (!IsRelevantVideo(video)) continue;

                // Opcional: pular se UploadedAt <= lastProcessedDate (economiza quota)
                if (lastProcessedDate.HasValue && video.UploadedAt <= lastProcessedDate.Value)
                    continue;

                await ProcessVideoAsync(video);
                processedCount++;

                if (video.UploadedAt > maxUploadedThisRun)
                    maxUploadedThisRun = video.UploadedAt ?? DateTime.MinValue;
            }

            if (string.IsNullOrEmpty(response.NextPageToken)) break;

            // Salva token para continuação no próximo dia
            await progressoRepository.SaveProgressoAsync(config.ChannelId ?? "mine", playlistId, response.NextPageToken, null);

            request.PageToken = response.NextPageToken;

            // Pequeno delay para ajudar a não bater rate limit por segundo (YouTube tem limites por segundo além da quota diária)
            await Task.Delay(1200);  // ~0.8 req/s
        }

        // Salvar data máxima processada neste run (se houver vídeos relevantes)
        if (maxUploadedThisRun > DateTime.MinValue)
        {
            await progressoRepository.SaveProgressoAsync(
                channelKey,
                playlistId,
                lastPageToken,
                maxUploadedThisRun
            );
            logger.LogInformation("Data máxima processada atualizada para {MaxDate} no canal {ChannelKey}", maxUploadedThisRun, channelKey);
        }
    }

    private async Task<string> GetPlaylistIdAsync(ChannelConfig config)
    {
        if (!string.IsNullOrEmpty(config.PlaylistId))
            return config.PlaylistId;

        var request = youtubeService.Channels.List("contentDetails");
        if (config.Mine)
            request.Mine = true;
        else
            request.Id = config.ChannelId;

        var response = await youtubeApiService.ListChannelsAsync(request);
        return response.Items?.FirstOrDefault()?.ContentDetails?.RelatedPlaylists?.Uploads ?? string.Empty;
    }

    private Core.Models.Video MapToVideoModel(PlaylistItem item, IList<Google.Apis.YouTube.v3.Data.Video> details)
    {
        var detail = details.FirstOrDefault(v => v.Id == item.Snippet?.ResourceId?.VideoId);

        return new Core.Models.Video
        {
            Id = item.Snippet?.ResourceId?.VideoId ?? "",
            PlaylistId = item.Snippet?.PlaylistId ?? "",
            ChannelId = item.Snippet?.ChannelId ?? "",
            ChannelTitle = item.Snippet?.ChannelTitle ?? "",
            Title = item.Snippet?.Title ?? "",
            Description = item.Snippet?.Description ?? "",
            Length = detail?.ContentDetails?.Duration is not null
                ? XmlConvert.ToTimeSpan(detail.ContentDetails.Duration).ToString()
                : "",
            PrivacyStatus = item.Status?.PrivacyStatus ?? "",
            UploadedAt = item.Snippet?.PublishedAtDateTimeOffset?.LocalDateTime ?? DateTime.MinValue
        };
    }

    private bool IsRelevantVideo(Core.Models.Video v)
    {
        string title = v.Title?.ToUpperInvariant() ?? "";
        return title.Contains("CULTO") ||
               title.Contains("MINISTÉRIO") ||
               title.StartsWith("GIOM");
    }

    private async Task ProcessVideoAsync(Core.Models.Video video)
    {
        try
        {
            var existing = await resumoVideoRepository.GetVideoAsync(video.Id);
            if (existing != null)
            {
                video.Legenda = existing.Legenda;
                video.Resumo = existing.Resumo;
            }

            if (video.Legenda == null && (_runtimeOptions.ShouldGetCaption || _runtimeOptions.ShouldGenerateSummary))
            {
                video.Legenda = await GetVideoCaptionAsync(video.Id);
            }

            if (video.Resumo == null && _runtimeOptions.ShouldGenerateSummary)
            {
                video.Resumo = await GenerateSummaryAsync(video.Id);
            }

            if (existing == null)
                await resumoVideoRepository.SaveVideoAsync(video);
            else
                await resumoVideoRepository.UpdateVideoAsync(video);

            logger.LogInformation("Processado: {Title} ({Id})", video.Title, video.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Erro ao processar vídeo {Id} - {Title}", video.Id, video.Title);
        }

        // Delay mantido, mas reduzido um pouco
        await Task.Delay(3000);
    }

    private async Task<Core.Models.LegendaVideo> GetVideoCaptionAsync(string videoId)
    {
        var listRequest = youtubeService.Captions.List("snippet", videoId);
        var listResponse = await youtubeApiService.ListCaptionsAsync(listRequest);

        var caption = listResponse.Items
            .FirstOrDefault(c => c.Snippet?.Status != "failed");

        if (caption == null) return null;

        var downloadRequest = youtubeService.Captions.Download(caption.Id);
        downloadRequest.Tfmt = "sbv";

        using var memory = new MemoryStream();
        await youtubeApiService.DownloadCaptionAsync(downloadRequest, memory);

        return new Core.Models.LegendaVideo
        {
            Descricao = System.Text.Encoding.UTF8.GetString(memory.ToArray()),
            Formato = "sbv",
            VideoId = videoId
        };
    }

    private async Task<Core.Models.ResumoVideo> GenerateSummaryAsync(string videoId)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("Origin", "https://www.summarize.tech");
        http.DefaultRequestHeaders.Referrer = new Uri($"https://www.summarize.tech/www.youtube.com/watch?v={videoId}");

        var payload = new
        {
            url = $"https://youtube.com/watch?v={videoId}",
            deviceId = Guid.NewGuid().ToString(),
            idToken = ""
        };

        var content = new StringContent(JsonConvert.SerializeObject(payload), System.Text.Encoding.UTF8, "application/json");

        var response = await http.PostAsync("https://www.summarize.tech/api/summary", content);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"summarize.tech retornou {response.StatusCode}");

            deviceId = Guid.NewGuid().ToString();
            return await GenerateSummaryAsync(videoId);
        }

        var jsonContent = await response.Content.ReadAsStringAsync();
        var summary = JsonConvert.DeserializeObject<SummaryParts>(jsonContent);
        var rollup = summary?.rollups?
            .Select(x =>
            {
                if (string.IsNullOrEmpty(x.Value.summary.content))
                    x.Value.summary.content = x.Value.summary.before + x.Value.summary.keyword + x.Value.summary.after;
                return x;
            })
            .Aggregate((lr, rr) =>
            {
                rr.Value.children.All(rc => { lr.Value.children.Add(rc.Key, rc.Value); return true; });
                rr.Value.summary.content += lr.Value.summary.content;
                return lr;
            }).Value;
        if (rollup != null)
        {
            return new ResumoVideo
            {
                VideoId = videoId,
                Descricao = TranslateTextAsync(rollup.summary.content).Result,
                Capitulos = rollup.children.Select(child =>
                    new CapituloResumoVideo
                    {
                        Inicio = TimeSpan.FromSeconds(double.Parse(child.Key)),
                        Resumo = TranslateTextAsync(child.Value).Result
                    }).ToArray()
            };
        }

        return null;
    }

    private async Task<string> TranslateTextAsync(string text)
    {
        TranslateTextRequest request = new TranslateTextRequest
        {
            Parent = new ProjectName(ProjectId).ToString(),
            Contents = { text },
            SourceLanguageCode = "en-US",
            TargetLanguageCode = "pt-BR",
        };

        TranslateTextResponse response = await translationClient.TranslateTextAsync(request);

        if (response.Translations.Count > 0)
        {
            return response.Translations.First().TranslatedText;
        }

        return null;
    }

    private async Task<IList<Google.Apis.YouTube.v3.Data.Video>> GetVideosDetailsAsync(IReadOnlyList<string> ids)
    {
        var request = youtubeService.Videos.List("contentDetails,snippet,status");
        request.Id = string.Join(",", ids);

        var response = await youtubeApiService.ListVideosAsync(request);
        return response.Items;
    }
}
