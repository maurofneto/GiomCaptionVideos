using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.ObservabilidadeTeste;

/// <summary>
/// Gera um "resumo" sintético (sem chamar Claude) — só pra exercitar a etapa de Resumo do
/// pipeline. Injeta falha determinística por vídeo (ver InjecaoDeFalha).
/// </summary>
public class FakeResumoService(ILogger<FakeResumoService> logger) : IResumoService
{
    public Task<Resumo?> GerarResumoAsync(Video video, CancellationToken ct = default)
    {
        switch (InjecaoDeFalha.Avaliar(video.Id, "resumo"))
        {
            case InjecaoDeFalha.Resultado.FalhaTransitoriaUmaVez:
                logger.LogWarning("FakeResumoService — simulando falha transitória (1x) pra {VideoId}", video.Id);
                throw new InvalidOperationException("Falha simulada (transitória, deve suceder no retry)");
            case InjecaoDeFalha.Resultado.FalhaTransitoriaSempre:
                logger.LogWarning("FakeResumoService — simulando falha transitória permanente pra {VideoId} — deve cair na DLQ", video.Id);
                throw new InvalidOperationException("Falha simulada (permanente, deve esgotar tentativas e cair na DLQ)");
            case InjecaoDeFalha.Resultado.RateLimitSempre:
                logger.LogWarning("FakeResumoService — simulando rate limit pra {VideoId}", video.Id);
                throw new RateLimitExcedidoException("Resumo.Fake", TimeSpan.FromSeconds(15));
        }

        return Task.FromResult<Resumo?>(new Resumo
        {
            VideoId  = video.Id,
            Descricao = $"Resumo sintético de teste E2E para \"{video.Title}\" — sem IA envolvida.",
            Origem   = "FakeResumoService (teste E2E)",
            GeradoEm = DateTime.UtcNow,
            Capitulos =
            [
                new Capitulo { Inicio = TimeSpan.Zero, Texto = "Abertura" },
                new Capitulo { Inicio = TimeSpan.FromMinutes(5), Texto = "Desenvolvimento" },
                new Capitulo { Inicio = TimeSpan.FromMinutes(15), Texto = "Encerramento" },
            ]
        });
    }
}
