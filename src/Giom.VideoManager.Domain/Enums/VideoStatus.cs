namespace Giom.VideoManager.Domain.Enums;

/// <summary>Situação do vídeo no YouTube.</summary>
public enum VideoStatus
{
    /// <summary>Vídeo presente na playlist do canal.</summary>
    Active,

    /// <summary>O YouTube devolveu "Deleted video" no lugar dele; os dados já gravados são mantidos.</summary>
    Removed
}
