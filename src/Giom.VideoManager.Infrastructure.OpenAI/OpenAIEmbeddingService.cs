using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Resiliencia;
using Giom.VideoManager.Infrastructure.Supabase;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System.Net.Http.Headers;
using System.Text;

namespace Giom.VideoManager.Infrastructure.OpenAI;

public class OpenAiEmbeddingOptions
{
    public string OpenAiApiKey { get; init; } = string.Empty;
    public string EmbeddingModel { get; init; } = "text-embedding-3-small";
    public int ChunkSize { get; init; } = 2400;
    public int ChunkOverlap { get; init; } = 400;
    public int EmbedBatchSize { get; init; } = 20;
}

/// <summary>
/// Processa tudo em C# sem Edge Functions:
///   - Busca vídeo/transcrição via IVideoRepository
///   - Chunking local
///   - Embeddings via OpenAI API
///   - Persistência via IVideoChunkRepository
///
/// Busca semântica delega à Edge Function buscar-similar (leve — 1 embedding + 1 query).
/// </summary>
public class OpenAIEmbeddingService : IEmbeddingService
{
    /// <summary>
    /// Tag gravada em cada chunk e enviada em toda busca — o Supabase só compara embeddings da
    /// MESMA origem (Feature #101), já que vetores de provedores diferentes não são comparáveis
    /// por similaridade de cosseno entre si.
    /// </summary>
    private const string Origem = "OpenAI";

    private readonly OpenAiEmbeddingOptions _options;
    private readonly IVideoChunkRepository _chunkRepository;
    private readonly ILogger<OpenAIEmbeddingService> _logger;
    private readonly IContextoTelemetria _contextoTelemetria;
    private readonly HttpClient _openAi;
    private readonly HttpClient _supabaseFunctions;

    public OpenAIEmbeddingService(
        IOptions<OpenAiEmbeddingOptions> options,
        IOptions<SupabaseOptions> supabaseOptions,
        IVideoChunkRepository chunkRepository,
        IHttpClientFactory httpClientFactory,
        ILogger<OpenAIEmbeddingService> logger,
        IContextoTelemetria contextoTelemetria)
    {
        _options         = options.Value;
        _chunkRepository = chunkRepository;
        _logger          = logger;
        _contextoTelemetria = contextoTelemetria;

        _openAi = httpClientFactory.CreateClient("openai");
        _openAi.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _options.OpenAiApiKey);

        // Apenas para chamar Edge Functions (buscar-similar) — não acessa tabelas diretamente
        var sb = supabaseOptions.Value;
        _supabaseFunctions = httpClientFactory.CreateClient("supabase-functions");
        _supabaseFunctions.BaseAddress = new Uri(sb.Url);
        _supabaseFunctions.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", sb.SecretKey);
        _supabaseFunctions.DefaultRequestHeaders.Add("apikey", sb.SecretKey);
    }

    // =========================================================
    // Indexação
    // =========================================================

    public async Task IndexarAsync(Video video, CancellationToken ct = default)
    {
        if (video.Chunks.Count > 0)
        {
            _logger.LogInformation("{VideoId} já tem {Count} chunks — indexação ignorada", video.Id, video.Chunks.Count);
            return;
        }

        if (video.Transcricao == null)
        {
            _logger.LogWarning("Transcrição não encontrada para {VideoId} — indexação ignorada", video.Id);
            return;
        }

        _logger.LogInformation("Iniciando indexação direta: {VideoId}", video.Id);

        var conteudo = video.Transcricao.ConteudoLimpo;
        _logger.LogDebug("{VideoId} — conteúdo para embedding ({Chars} chars), primeiros 300: {Preview}",
            video.Id, conteudo.Length,
            conteudo.Length > 300 ? conteudo[..300] : conteudo);

        var chunks = TextChunker.Dividir(conteudo, _options.ChunkSize, _options.ChunkOverlap);
        _logger.LogInformation("{VideoId} — {Chunks} chunks de {Chars} chars",
            video.Id, chunks.Count, conteudo.Length);

        var (embeddings, tokensTotais) = await GenerateEmbeddingsAsync(chunks, ct);

        _contextoTelemetria.Registrar(
            provedor: "OpenAI",
            modelo: _options.EmbeddingModel,
            tokensEntrada: tokensTotais,
            custoEstimadoUsd: OpenAiPricingCalculator.Calcular(_options.EmbeddingModel, tokensTotais));

        await _chunkRepository.DeletarPorVideoAsync(video.Id, ct);
        await _chunkRepository.InserirAsync(
            chunks.Select((mem, i) => new VideoChunk
            {
                VideoId    = video.Id,
                Titulo     = video.Title,
                ChunkIndex = i,
                Conteudo   = mem.ToString(),
                Embedding  = embeddings[i],
                Origem     = Origem
            }), ct);

        _logger.LogInformation("{VideoId} — {Chunks} chunks indexados com sucesso", video.Id, chunks.Count);
    }

    // =========================================================
    // Busca — embeda a pergunta aqui mesmo (client-side) e delega o resto (RRF híbrido,
    // diversidade por aula) pra Edge Function buscar-similar, passando embedding+origem
    // (Feature #101) — o Supabase filtra por origem antes de comparar vetores.
    // =========================================================

    public async Task<BuscaSemanticaResultado> BuscarAsync(
        string pergunta,
        string? area = null,
        string? canalId = null,
        int limite = 6,
        double threshold = 0.35,
        CancellationToken ct = default)
    {
        var (embeddings, _) = await GenerateEmbeddingsAsync([pergunta.AsMemory()], ct);
        var embedding = embeddings[0];

        var payload = new { pergunta, embedding, origem = Origem, area, limite, threshold, canal_id = canalId };
        var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

        var response = await _supabaseFunctions.PostAsync("functions/v1/buscar-similar", content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Falha na busca semântica: {Status} — {Body}", response.StatusCode, body);
            return new BuscaSemanticaResultado([], "Nenhuma aula relevante encontrada.");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        var result = JsonConvert.DeserializeAnonymousType(json, new
        {
            chunks = new[] { new
            {
                video_id     = "",
                titulo       = "",
                chunk_index  = 0,
                conteudo     = "",
                area         = (string?)null,
                subcategoria = (string?)null,
                similarity   = 0.0
            }},
            contexto_formatado = ""
        });

        var resultChunks = result?.chunks?
            .Select(c => new ChunkSimilar(c.video_id, c.titulo, c.chunk_index, c.conteudo, c.area, c.subcategoria, c.similarity))
            .ToList() ?? [];

        return new BuscaSemanticaResultado(resultChunks, result?.contexto_formatado ?? "");
    }

    // =========================================================
    // Helpers privados
    // =========================================================

    private async Task<(List<float[]> Embeddings, int TokensTotais)> GenerateEmbeddingsAsync(List<ReadOnlyMemory<char>> chunks, CancellationToken ct)
    {
        var all = new List<float[]>();
        var tokensTotais = 0;

        for (int i = 0; i < chunks.Count; i += _options.EmbedBatchSize)
        {
            // ToString() aqui: única alocação por chunk, feita batch a batch
            var batch = chunks.Skip(i).Take(_options.EmbedBatchSize)
                              .Select(m => m.ToString())
                              .ToList();

            var payload = new { model = _options.EmbeddingModel, input = batch };
            var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            var response = await _openAi.PostAsync("v1/embeddings", content, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                // StatusCode explícito no ctor — é o que o SqsFilaConsumer usa pra reconhecer
                // 429 (rate limit) e aplicar o delay dinâmico (US #44), sem este projeto
                // conhecer nada de fila/retry.
                throw new HttpRequestException($"OpenAI embeddings falhou: {response.StatusCode} — {json}", inner: null, statusCode: response.StatusCode);

            var result = JsonConvert.DeserializeAnonymousType(json, new
            {
                data = new[] { new { index = 0, embedding = new float[0] } },
                usage = new { total_tokens = 0 }
            });

            all.AddRange(result!.data.OrderBy(d => d.index).Select(d => d.embedding));
            tokensTotais += result.usage?.total_tokens ?? 0;

            if (i + _options.EmbedBatchSize < chunks.Count)
                await Task.Delay(200, ct);

            _logger.LogDebug("Embeddings: {Done}/{Total}",
                Math.Min(i + _options.EmbedBatchSize, chunks.Count), chunks.Count);
        }

        return (all, tokensTotais);
    }
}
