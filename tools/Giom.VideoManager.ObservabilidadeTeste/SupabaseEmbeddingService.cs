using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Supabase;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Giom.VideoManager.ObservabilidadeTeste;

/// <summary>
/// Mesmo padrão de SupabaseCaptionService/SupabaseResumoService/SupabaseCategorizacaoService:
/// reaproveita os chunks + embeddings REAIS já indexados (por OpenAI, em execução anterior de
/// verdade) pro vídeo de origem — video.PlaylistId carrega o Id real, ver SupabaseVideoSource.
/// Zero chamada OpenAI; a coluna `embedding` (vector(1536)) volta da REST API do Supabase como
/// texto "[0.1,0.2,...]" (sem tipo JSON nativo pra vector), por isso o parse manual abaixo.
///
/// Injeta falha determinística por vídeo de ORIGEM (mesmo Id entre reentregas da mesma mensagem
/// SQS, ao contrário de video.Id que é sempre novo por run), pra exercitar o retry dinâmico da
/// Feature #34 (US #44/#45) sem depender de sorte:
///   - hash % 10 == 0: falha transitória na 1ª tentativa, sucesso na 2ª — cobre o retry rápido
///     (RetryDelaySeconds) do SqsFilaConsumer.
///   - hash % 10 == 1: RateLimitExcedidoException sempre — cobre o delay de rate limit e,
///     depois de MaxReceiveCount tentativas, o vídeo indo pra DLQ.
///   - demais: sucesso direto.
/// </summary>
public class SupabaseEmbeddingService : IEmbeddingService
{
    private readonly HttpClient _http;
    private readonly IVideoChunkRepository _chunkRepository;
    private readonly ILogger<SupabaseEmbeddingService> _logger;
    private static readonly ConcurrentDictionary<string, int> Tentativas = new();

    public SupabaseEmbeddingService(IOptions<SupabaseOptions> options, IHttpClientFactory httpClientFactory, IVideoChunkRepository chunkRepository, ILogger<SupabaseEmbeddingService> logger)
    {
        var opcoes = options.Value;
        _http = httpClientFactory.CreateClient("supabase");
        _http.BaseAddress = new Uri(opcoes.Url);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", opcoes.SecretKey);
        _http.DefaultRequestHeaders.Add("apikey", opcoes.SecretKey);
        _chunkRepository = chunkRepository;
        _logger = logger;
    }

    public async Task IndexarAsync(Video video, CancellationToken ct = default)
    {
        var idOrigem = video.PlaylistId;
        if (string.IsNullOrEmpty(idOrigem)) return;

        var bucket = (int)((uint)idOrigem.GetHashCode() % 10);
        var tentativa = Tentativas.AddOrUpdate(idOrigem, 1, (_, n) => n + 1);

        if (bucket == 1)
        {
            _logger.LogWarning("SupabaseEmbeddingService — simulando quota excedida pra {VideoId} (origem {Origem}, tentativa {Tentativa})", video.Id, idOrigem, tentativa);
            throw new RateLimitExcedidoException("Embedding.Fake", TimeSpan.FromSeconds(15));
        }

        if (bucket == 0 && tentativa == 1)
        {
            _logger.LogWarning("SupabaseEmbeddingService — simulando falha transitória pra {VideoId} (origem {Origem}, 1ª tentativa)", video.Id, idOrigem);
            throw new InvalidOperationException("Falha simulada (edge case de retry) — deve suceder na próxima tentativa");
        }

        var response = await _http.GetAsync($"/rest/v1/video_chunk?video_id=eq.{idOrigem}&select=chunk_index,conteudo,embedding&order=chunk_index.asc", ct);
        if (!response.IsSuccessStatusCode) return;

        var json = await response.Content.ReadAsStringAsync(ct);
        var linhas = JsonConvert.DeserializeObject<List<ChunkRowDto>>(json) ?? [];
        if (linhas.Count == 0) return;

        var chunks = linhas.Select(linha => new VideoChunk
        {
            VideoId    = video.Id,
            Titulo     = video.Title,
            ChunkIndex = linha.chunk_index,
            Conteudo   = linha.conteudo,
            Embedding  = ParsearVetor(linha.embedding)
        }).ToList();

        await _chunkRepository.DeletarPorVideoAsync(video.Id, ct);
        await _chunkRepository.InserirAsync(chunks, ct);

        _logger.LogInformation("SupabaseEmbeddingService — {VideoId}: {Count} chunks reaproveitados de {Origem} (tentativa {Tentativa})", video.Id, chunks.Count, idOrigem, tentativa);
    }

    public Task<BuscaSemanticaResultado> BuscarAsync(string pergunta, string? area = null, string? canalId = null, int limite = 6, double threshold = 0.35, CancellationToken ct = default)
        => Task.FromResult(new BuscaSemanticaResultado([], "Busca semântica não simulada nesta ferramenta."));

    /// <summary>PostgREST devolve vector como texto "[0.1,0.2,...]" — sem tipo JSON nativo pra vector.</summary>
    private static float[] ParsearVetor(string textoVetor) =>
        textoVetor.Trim('[', ']')
                  .Split(',', StringSplitOptions.RemoveEmptyEntries)
                  .Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture))
                  .ToArray();

    private record ChunkRowDto(int chunk_index, string conteudo, string embedding);
}
