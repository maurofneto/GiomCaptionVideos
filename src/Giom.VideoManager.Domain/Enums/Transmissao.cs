namespace Giom.VideoManager.Domain.Enums;

/// <summary>
/// Situação da transmissão ao vivo de um vídeo no YouTube. Permite regras de relevância como
/// "ignorar live ainda não transmitida" (uma live agendada não tem legenda nem áudio pra baixar).
/// </summary>
public enum Transmissao
{
    /// <summary>Vídeo comum, sem transmissão ao vivo associada.</summary>
    Nenhuma,

    /// <summary>Live ou estreia agendada que ainda não começou.</summary>
    Agendada,

    /// <summary>Transmissão em andamento.</summary>
    AoVivo,

    /// <summary>Transmissão já encerrada (a gravação ficou disponível).</summary>
    Encerrada
}
