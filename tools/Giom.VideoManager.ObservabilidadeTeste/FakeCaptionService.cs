using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.ObservabilidadeTeste;

/// <summary>
/// Nunca é chamado de verdade — o FakeVideoSourceService já entrega o vídeo com Transcricao
/// preenchida, então InstitutoVideoHandler pula a etapa de legenda. Existe só pra satisfazer o
/// DI do InstitutoVideoHandler sem precisar registrar o YouTubeCaptionService real (que exige
/// credencial OAuth do Google, sem sentido pra um teste 100% fake).
/// </summary>
public class FakeCaptionService : ICaptionService
{
    public Task<Transcricao?> GetCaptionAsync(Video video, CancellationToken ct = default)
        => throw new InvalidOperationException("FakeCaptionService não deveria ser chamado — o vídeo fake já vem com Transcricao preenchida.");
}
