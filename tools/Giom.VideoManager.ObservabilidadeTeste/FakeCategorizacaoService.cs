using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Enums;
using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.ObservabilidadeTeste;

/// <summary>
/// Classifica com categorias sintéticas (sem chamar Claude) — só pra exercitar a etapa de
/// Categorização do pipeline. Injeta falha determinística por vídeo (ver InjecaoDeFalha).
/// </summary>
public class FakeCategorizacaoService(ILogger<FakeCategorizacaoService> logger) : ICategorizacaoService
{
    private static readonly AreaTeologica[] Areas = Enum.GetValues<AreaTeologica>();

    public Task<IReadOnlyList<CategoriaTeologica>> ClassificarAsync(Video video, CancellationToken ct = default)
    {
        switch (InjecaoDeFalha.Avaliar(video.Id, "categorizacao"))
        {
            case InjecaoDeFalha.Resultado.FalhaTransitoriaUmaVez:
                logger.LogWarning("FakeCategorizacaoService — simulando falha transitória (1x) pra {VideoId}", video.Id);
                throw new InvalidOperationException("Falha simulada (transitória, deve suceder no retry)");
            case InjecaoDeFalha.Resultado.FalhaTransitoriaSempre:
                logger.LogWarning("FakeCategorizacaoService — simulando falha transitória permanente pra {VideoId} — deve cair na DLQ", video.Id);
                throw new InvalidOperationException("Falha simulada (permanente, deve esgotar tentativas e cair na DLQ)");
            case InjecaoDeFalha.Resultado.RateLimitSempre:
                logger.LogWarning("FakeCategorizacaoService — simulando rate limit pra {VideoId}", video.Id);
                throw new RateLimitExcedidoException("Categorizacao.Fake", TimeSpan.FromSeconds(15));
        }

        var area = Areas[Math.Abs(video.Id.GetHashCode()) % Areas.Length];

        IReadOnlyList<CategoriaTeologica> categorias =
        [
            new CategoriaTeologica
            {
                VideoId = video.Id,
                Area = area,
                Subcategoria = "Teste E2E",
                Peso = 1.0m,
                ClassificadoEm = DateTime.UtcNow
            }
        ];

        return Task.FromResult(categorias);
    }
}
