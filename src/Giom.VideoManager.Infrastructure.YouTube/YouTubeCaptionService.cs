using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Domain.Interfaces;
using Google.Apis.YouTube.v3;
using Giom.VideoManager.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Infrastructure.YouTube;

public class YouTubeCaptionOptions
{
    /// <summary>Formato da legenda baixada. Valores aceitos: vtt, sbv, srt. Padrão: vtt.</summary>
    public string Formato { get; init; } = "vtt";
}

/// <summary>
/// Baixa a legenda do vídeo diretamente da YouTube Data API (sem download de áudio).
/// Desde a US #146, resolve <see cref="YouTubeService"/> keyed por <see cref="Video.ChannelId"/>
/// (mesmo padrão da Ingestão #105) — cada canal autenticado com sua própria OAuth.
/// Sem keyed pro canal, lança <see cref="CredencialCanalIndisponivelException"/> — nunca usa
/// credencial global.
/// </summary>
public class YouTubeCaptionService : ICaptionService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly YouTubeCaptionOptions _options;
    private readonly ILogger<YouTubeCaptionService> _logger;

    public YouTubeCaptionService(
        IServiceProvider serviceProvider,
        IOptions<YouTubeCaptionOptions> options,
        ILogger<YouTubeCaptionService> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _logger  = logger;
    }

    /// <summary>Delay-base do retry de 5xx do Resilient. Só os testes mexem (evita esperar o backoff real).</summary>
    internal TimeSpan? RetryDelay { get; set; }

    public async Task<Transcricao?> GetCaptionAsync(Video video, CancellationToken ct = default)
    {
        try
        {
            return await ObterAsync(video);
        }
        catch (Exception ex)
        {
            var falha = ClassificadorFalhaCaption.Classificar(ex, ct);
            if (falha is null)
                throw;

            // Ponto único de log: uma falha classificada = um log, no nível da classe.
            FalhaCaptionLog.Registrar(_logger, falha, Provedor, video.Id, video.ChannelId);

            // Exceções que já têm tipo próprio na cadeia (quota, credencial do canal, classificada) seguem como estão.
            if (ex is CaptionFalhaException or RateLimitExcedidoException or CredencialCanalIndisponivelException)
                throw;

            throw falha.Classe switch
            {
                ClasseFalhaCaption.QuotaExcedida => new RateLimitExcedidoException(Provedor, innerException: ex),
                _ => new CaptionFalhaException(falha.Classe, Provedor, $"{falha.Classe} (HTTP {falha.HttpStatus?.ToString() ?? "?"}, {falha.Reason ?? "sem reason"}) no vídeo {video.Id}",
                    falha.HttpStatus, falha.Reason, ex)
            };
        }
    }

    private const string Provedor = "YouTubeCaption";

    private async Task<Transcricao?> ObterAsync(Video video)
    {
        var youtube = ResolverYouTube(video.ChannelId);
        var api = new ResilientYouTubeApiService(youtube, Microsoft.Extensions.Logging.Abstractions.NullLogger<ResilientYouTubeApiService>.Instance, RetryDelay);

        var listRequest  = youtube.Captions.List("snippet", video.Id);
        var listResponse = await api.ListCaptionsAsync(listRequest);

        var caption = listResponse.Items?.FirstOrDefault(c => c.Snippet?.Status != "failed");
        if (caption == null)
            throw new CaptionFalhaException(ClasseFalhaCaption.SemLegendaAinda, Provedor, $"nenhuma legenda disponível pro vídeo {video.Id}");

        var downloadRequest  = youtube.Captions.Download(caption.Id);
        downloadRequest.Tfmt = _options.Formato;

        using var memory = new MemoryStream();
        await api.DownloadCaptionAsync(downloadRequest, memory);

        var conteudo = System.Text.Encoding.UTF8.GetString(memory.ToArray());
        if (string.IsNullOrWhiteSpace(conteudo))
            throw new CaptionFalhaException(ClasseFalhaCaption.LegendaVaziaReal, Provedor, $"legenda baixada veio vazia pro vídeo {video.Id}", httpStatus: 200);

        _logger.LogInformation("Legenda obtida via YouTubeCaption: {VideoId} ({Formato}, {Chars} chars)",
            video.Id, _options.Formato, conteudo.Length);

        return new Transcricao
        {
            VideoId       = video.Id,
            Conteudo      = conteudo,
            ConteudoLimpo = TranscricaoHelper.StripTimestamps(conteudo, _options.Formato),
            Formato       = _options.Formato,
            Origem        = "YouTubeCaption",
            GeradoEm      = DateTime.UtcNow
        };
    }

    internal YouTubeService ResolverYouTube(string? channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId))
            throw new CredencialCanalIndisponivelException("(vazio)", "vídeo sem ChannelId");

        var keyed = _serviceProvider.GetKeyedService<YouTubeService>(channelId);
        if (keyed is not null)
            return keyed;

        // Sem fallback pra credencial global: um canal sem keyed falhou no boot (ou não está em
        // canal_ingestao ativo). Erro classificável com o motivo real, pro FallbackHandler seguir
        // pro próximo elo e o log dizer a causa em vez de "No service for type YouTubeService".
        var motivo = _serviceProvider.GetService<FalhasCredencialPorCanal>() is { } falhas && falhas.TentarObterMotivo(channelId, out var m)
            ? m
            : "canal sem credencial registrada no boot (fora de canal_ingestao ativo?)";
        throw new CredencialCanalIndisponivelException(channelId, motivo);
    }
}
