using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Infrastructure.YouTube;

/// <summary>
/// Um único log estruturado por falha classificada. Propriedades: Classe, Provedor, VideoId,
/// ChannelId, HttpStatus, Reason. Nunca recebe token, cookie, URL com auth nem corpo de resposta.
/// </summary>
internal static class FalhaCaptionLog
{
    public const string Template =
        "Falha de legenda classificada: {Classe} Provedor={Provedor} VideoId={VideoId} ChannelId={ChannelId} HttpStatus={HttpStatus} Reason={Reason}";

    public static LogLevel NivelDe(ClasseFalhaCaption classe) => classe switch
    {
        ClasseFalhaCaption.SemPermissao or ClasseFalhaCaption.CredencialInvalida or ClasseFalhaCaption.BloqueioBot => LogLevel.Error,
        ClasseFalhaCaption.QuotaExcedida or ClasseFalhaCaption.Transitorio or ClasseFalhaCaption.LegendaVaziaReal => LogLevel.Warning,
        _ => LogLevel.Information
    };

    public static void Registrar(ILogger logger, FalhaCaptionClassificada falha, string provedor, string videoId, string? channelId) =>
        logger.Log(NivelDe(falha.Classe), Template, falha.Classe, provedor, videoId, channelId, falha.HttpStatus, falha.Reason);
}
