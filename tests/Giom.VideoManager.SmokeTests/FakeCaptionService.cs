using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.SmokeTests;

/// <summary>
/// Gera uma "legenda" sintética (texto fixo, repetido até um tamanho realista) — sem YouTube,
/// sem depender de nenhum dado pré-existente no Supabase. Injeta falha determinística por vídeo
/// (ver InjecaoDeFalha) pra exercitar retry/DLQ na etapa de Transcrição.
/// </summary>
public class FakeCaptionService(ILogger<FakeCaptionService> logger) : ICaptionService
{
    private const string Paragrafo =
        "Hoje vamos refletir sobre a graça de Deus manifesta na vida da igreja. " +
        "A Palavra nos ensina que somos chamados para viver em comunhão uns com os outros, " +
        "edificando o corpo de Cristo com amor e verdade. ";

    // Domain.Entities.Transcricao qualificado — desde a Feature #104, o projeto Transcricao.Worker
    // também é referenciado aqui, e "Giom.VideoManager.Transcricao" (namespace do worker) colide
    // com "Transcricao" (a entidade) na resolução de nome sem qualificação.
    public Task<Domain.Entities.Transcricao?> GetCaptionAsync(Video video, CancellationToken ct = default)
    {
        switch (InjecaoDeFalha.Avaliar(video.Id, "transcricao"))
        {
            case InjecaoDeFalha.Resultado.FalhaTransitoriaUmaVez:
                logger.LogWarning("FakeCaptionService — simulando falha transitória (1x) pra {VideoId}", video.Id);
                throw new InvalidOperationException("Falha simulada (transitória, deve suceder no retry)");
            case InjecaoDeFalha.Resultado.FalhaTransitoriaSempre:
                logger.LogWarning("FakeCaptionService — simulando falha transitória permanente pra {VideoId} — deve cair na DLQ", video.Id);
                throw new InvalidOperationException("Falha simulada (permanente, deve esgotar tentativas e cair na DLQ)");
            case InjecaoDeFalha.Resultado.RateLimitSempre:
                logger.LogWarning("FakeCaptionService — simulando rate limit pra {VideoId}", video.Id);
                throw new RateLimitExcedidoException("Transcricao.Fake", TimeSpan.FromSeconds(15));
        }

        var conteudo = string.Concat(Enumerable.Repeat(Paragrafo, 20));

        return Task.FromResult<Domain.Entities.Transcricao?>(new Domain.Entities.Transcricao
        {
            VideoId       = video.Id,
            Conteudo      = conteudo,
            ConteudoLimpo = conteudo,
            Formato       = "txt",
            Origem        = "FakeCaptionService (teste E2E)",
            GeradoEm      = DateTime.UtcNow
        });
    }
}
