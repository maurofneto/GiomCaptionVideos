using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.SmokeTests;

/// <summary>
/// Gera chunks com vetores sintéticos (determinísticos, sem chamar OpenAI) e persiste de
/// verdade no Supabase de dev via IVideoChunkRepository — é essa escrita real que valida
/// span/telemetria/Helicone ponta a ponta (propósito do Board #96). Dimensão fixa em 1536 pra
/// bater com a coluna vector(1536) do banco. Injeta falha determinística por vídeo (ver
/// InjecaoDeFalha) pra exercitar o retry dinâmico da Feature #34.
/// </summary>
public class FakeEmbeddingService(IVideoChunkRepository chunkRepository, ILogger<FakeEmbeddingService> logger) : IVideoIndexingService
{
    private const int Dimensoes = 1536;

    public async Task IndexarAsync(Video video, CancellationToken ct = default)
    {
        switch (InjecaoDeFalha.Avaliar(video.Id, "embedding"))
        {
            case InjecaoDeFalha.Resultado.FalhaTransitoriaUmaVez:
                logger.LogWarning("FakeEmbeddingService — simulando falha transitória (1x) pra {VideoId}", video.Id);
                throw new InvalidOperationException("Falha simulada (transitória, deve suceder no retry)");
            case InjecaoDeFalha.Resultado.FalhaTransitoriaSempre:
                logger.LogWarning("FakeEmbeddingService — simulando falha transitória permanente pra {VideoId} — deve cair na DLQ", video.Id);
                throw new InvalidOperationException("Falha simulada (permanente, deve esgotar tentativas e cair na DLQ)");
            case InjecaoDeFalha.Resultado.RateLimitSempre:
                logger.LogWarning("FakeEmbeddingService — simulando rate limit pra {VideoId}", video.Id);
                throw new RateLimitExcedidoException("Embedding.Fake", TimeSpan.FromSeconds(15));
        }

        var conteudo = video.Transcricao?.ConteudoLimpo ?? video.Description;
        var chunks = new[]
        {
            new VideoChunk
            {
                VideoId = video.Id,
                Titulo = video.Title,
                ChunkIndex = 0,
                Conteudo = conteudo,
                Embedding = GerarVetorDeterministico(video.Id, 0)
            }
        };

        await chunkRepository.DeletarPorVideoAsync(video.Id, ct);
        await chunkRepository.InserirAsync(chunks, ct);
    }

    private static float[] GerarVetorDeterministico(string videoId, int chunkIndex)
    {
        var seed = HashCode.Combine(videoId, chunkIndex);
        var random = new Random(seed);
        var vetor = new float[Dimensoes];
        for (var i = 0; i < Dimensoes; i++)
            vetor[i] = (float)(random.NextDouble() * 2 - 1);
        return vetor;
    }
}
