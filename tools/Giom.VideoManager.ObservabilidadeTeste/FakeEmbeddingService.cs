using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.ObservabilidadeTeste;

/// <summary>
/// Substitui DirectEmbeddingService (OpenAI real) — chunking simples e vetores fake
/// (vector(1536) no Supabase, dimensão precisa bater) derivados deterministicamente do conteúdo
/// via hash, sem custo/latência de API real.
///
/// Injeta falha determinística por vídeo de ORIGEM (video.PlaylistId — mesmo Id entre reentregas
/// da mesma mensagem SQS, ao contrário de video.Id que é sempre novo por run), pra exercitar o
/// retry dinâmico da Feature #34 (US #44/#45) sem depender de sorte:
///   - hash % 10 == 0: falha transitória na 1ª tentativa, sucesso na 2ª — cobre o retry rápido
///     (RetryDelaySeconds) do SqsFilaConsumer.
///   - hash % 10 == 1: RateLimitExcedidoException sempre — cobre o delay de rate limit e,
///     depois de MaxReceiveCount tentativas, o vídeo indo pra DLQ.
///   - demais: sucesso direto.
/// </summary>
public class FakeEmbeddingService(IVideoChunkRepository chunkRepository, ILogger<FakeEmbeddingService> logger) : IEmbeddingService
{
    private const int ChunkSize = 800;
    private static readonly ConcurrentDictionary<string, int> Tentativas = new();

    public async Task IndexarAsync(Video video, CancellationToken ct = default)
    {
        var conteudo = video.Transcricao?.ConteudoLimpo;
        if (string.IsNullOrWhiteSpace(conteudo)) return;

        var idOrigem = video.PlaylistId ?? video.Id;
        var bucket = (int)((uint)idOrigem.GetHashCode() % 10);
        var tentativa = Tentativas.AddOrUpdate(idOrigem, 1, (_, n) => n + 1);

        if (bucket == 1)
        {
            logger.LogWarning("FakeEmbeddingService — simulando quota excedida pra {VideoId} (origem {Origem}, tentativa {Tentativa})", video.Id, idOrigem, tentativa);
            throw new RateLimitExcedidoException("Embedding.Fake", TimeSpan.FromSeconds(15));
        }

        if (bucket == 0 && tentativa == 1)
        {
            logger.LogWarning("FakeEmbeddingService — simulando falha transitória pra {VideoId} (origem {Origem}, 1ª tentativa)", video.Id, idOrigem);
            throw new InvalidOperationException("Falha simulada (edge case de retry) — deve suceder na próxima tentativa");
        }

        var chunks = Chunkar(conteudo);
        var videoChunks = chunks.Select((texto, i) => new VideoChunk
        {
            VideoId    = video.Id,
            Titulo     = video.Title,
            ChunkIndex = i,
            Conteudo   = texto,
            Embedding  = VetorFake(texto)
        }).ToList();

        await chunkRepository.DeletarPorVideoAsync(video.Id, ct);
        await chunkRepository.InserirAsync(videoChunks, ct);

        logger.LogInformation("FakeEmbeddingService — {VideoId}: {Count} chunks fake indexados (tentativa {Tentativa})", video.Id, videoChunks.Count, tentativa);
    }

    public Task<BuscaSemanticaResultado> BuscarAsync(string pergunta, string? area = null, string? canalId = null, int limite = 6, double threshold = 0.35, CancellationToken ct = default)
        => Task.FromResult(new BuscaSemanticaResultado([], "Busca semântica não simulada nesta ferramenta."));

    private static List<string> Chunkar(string texto)
    {
        var chunks = new List<string>();
        for (var i = 0; i < texto.Length; i += ChunkSize)
            chunks.Add(texto.Substring(i, Math.Min(ChunkSize, texto.Length - i)));
        return chunks.Count > 0 ? chunks : [texto];
    }

    /// <summary>Vetor determinístico (mesmo texto → mesmo vetor) via hash SHA256 como seed — não tem semântica real, só precisa ter a dimensão certa (1536) pro insert no Supabase não falhar.</summary>
    private static float[] VetorFake(string texto)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(texto));
        var seed = BitConverter.ToInt32(hash, 0);
        var random = new Random(seed);
        var vetor = new float[1536];
        for (var i = 0; i < vetor.Length; i++)
            vetor[i] = (float)(random.NextDouble() * 2 - 1);
        return vetor;
    }
}
