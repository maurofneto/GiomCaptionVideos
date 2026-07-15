using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.SmokeTests;

/// <summary>
/// Gera um "resumo" sintético (sem chamar Claude) — só pra exercitar a etapa de Resumo do
/// pipeline. Injeta falha determinística por vídeo (ver InjecaoDeFalha).
/// </summary>
public class FakeResumoService(ILogger<FakeResumoService> logger) : IResumoService
{
    // Domain.Entities.Resumo qualificado — desde a Feature #104, o projeto Resumo.Worker
    // também é referenciado aqui, e "Giom.VideoManager.Resumo" (namespace do worker) colide com
    // "Resumo" (a entidade) na resolução de nome sem qualificação.
    public Task<Domain.Entities.Resumo?> GerarResumoAsync(Video video, CancellationToken ct = default)
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

        return Task.FromResult<Domain.Entities.Resumo?>(new Domain.Entities.Resumo
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
