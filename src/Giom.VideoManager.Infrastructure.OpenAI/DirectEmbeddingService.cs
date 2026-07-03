using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Supabase;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace Giom.VideoManager.Infrastructure.OpenAI;

public class DirectEmbeddingOptions
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
public class DirectEmbeddingService : IEmbeddingService
{
    private readonly DirectEmbeddingOptions _options;
    private readonly IVideoChunkRepository _chunkRepository;
    private readonly ILogger<DirectEmbeddingService> _logger;
    private readonly HttpClient _openAi;
    private readonly HttpClient _supabaseFunctions;

    public DirectEmbeddingService(
        IOptions<DirectEmbeddingOptions> options,
        IOptions<SupabaseOptions> supabaseOptions,
        IVideoChunkRepository chunkRepository,
        IHttpClientFactory httpClientFactory,
        ILogger<DirectEmbeddingService> logger)
    {
        _options         = options.Value;
        _chunkRepository = chunkRepository;
        _logger          = logger;

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

        var chunks = SplitIntoChunks(conteudo);
        _logger.LogInformation("{VideoId} — {Chunks} chunks de {Chars} chars",
            video.Id, chunks.Count, conteudo.Length);

        var embeddings = await GenerateEmbeddingsAsync(chunks, ct);

        await _chunkRepository.DeletarPorVideoAsync(video.Id, ct);
        await _chunkRepository.InserirAsync(
            chunks.Select((mem, i) => new VideoChunk
            {
                VideoId    = video.Id,
                Titulo     = video.Title,
                ChunkIndex = i,
                Conteudo   = mem.ToString(),
                Embedding  = embeddings[i]
            }), ct);

        _logger.LogInformation("{VideoId} — {Chunks} chunks indexados com sucesso", video.Id, chunks.Count);
    }

    // =========================================================
    // Busca — delega para a Edge Function buscar-similar
    // =========================================================

    public async Task<BuscaSemanticaResultado> BuscarAsync(
        string pergunta,
        string? area = null,
        int limite = 6,
        double threshold = 0.35,
        CancellationToken ct = default)
    {
        var payload = new { pergunta, area, limite, threshold };
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

    // Retorna spans sobre a string original — zero cópias intermediárias.
    // Cada ReadOnlyMemory<char> é apenas (ponteiro + offset + length) sobre `text`.
    private List<ReadOnlyMemory<char>> SplitIntoChunks(string text)
    {
        var chunks = new List<ReadOnlyMemory<char>>();
        var mem    = text.AsMemory();
        int start  = 0;

        while (start < text.Length)
        {
            int end = start + _options.ChunkSize;

            if (end < text.Length)
                end = FindGoodBreak(text, end);
            else
                end = text.Length;

            // Trim sem alocar: avança/recua os índices dentro do span
            var slice = mem.Slice(start, end - start);
            var span  = slice.Span;
            int trimStart = 0;
            while (trimStart < span.Length && char.IsWhiteSpace(span[trimStart])) trimStart++;
            int trimEnd = span.Length;
            while (trimEnd > trimStart && char.IsWhiteSpace(span[trimEnd - 1])) trimEnd--;

            if (trimEnd > trimStart)
                chunks.Add(slice.Slice(trimStart, trimEnd - trimStart));

            // Garante progresso mínimo de 1 char — evita loop infinito quando
            // FindGoodBreak recua muito e end - ChunkOverlap <= start anterior.
            int nextStart = end - _options.ChunkOverlap;
            start = nextStart > start ? nextStart : end;
            if (start >= text.Length) break;
        }

        return chunks;
    }

    // Opera sobre Span — sem alocar substring temporária.
    private static int FindGoodBreak(string text, int around)
    {
        int sliceStart = Math.Max(0, around - 200);
        int sliceEnd   = Math.Min(text.Length, around + 200);
        var span       = text.AsSpan(sliceStart, sliceEnd - sliceStart);
        int offset     = sliceStart;

        int paraBreak = span.LastIndexOf("\n\n".AsSpan(), StringComparison.Ordinal);
        if (paraBreak != -1) return offset + paraBreak + 2;

        int lineBreak = span.LastIndexOf('\n');
        if (lineBreak != -1) return offset + lineBreak + 1;

        // Regex ainda precisa de string — mas só é chamada como fallback raro
        var sentenceMatch = Regex.Match(span.ToString(), @"[.!?]\s");
        if (sentenceMatch.Success) return offset + sentenceMatch.Index + 2;

        return around;
    }

    private async Task<List<float[]>> GenerateEmbeddingsAsync(List<ReadOnlyMemory<char>> chunks, CancellationToken ct)
    {
        var all = new List<float[]>();

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
                throw new HttpRequestException($"OpenAI embeddings falhou: {response.StatusCode} — {json}");

            var result = JsonConvert.DeserializeAnonymousType(json, new
            {
                data = new[] { new { index = 0, embedding = new float[0] } }
            });

            all.AddRange(result!.data.OrderBy(d => d.index).Select(d => d.embedding));

            if (i + _options.EmbedBatchSize < chunks.Count)
                await Task.Delay(200, ct);

            _logger.LogDebug("Embeddings: {Done}/{Total}",
                Math.Min(i + _options.EmbedBatchSize, chunks.Count), chunks.Count);
        }

        return all;
    }
}
