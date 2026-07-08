namespace Giom.VideoManager.Infrastructure.Resiliencia;

/// <summary>
/// Corta um texto longo em pedaços de ~ChunkSize chars, preferindo quebrar em fronteira de
/// parágrafo/linha/frase (nunca no meio de uma palavra), com sobreposição (ChunkOverlap) entre
/// pedaços consecutivos — extraído de DirectEmbeddingService (OpenAI) pra ser reaproveitado por
/// qualquer provider de embedding (Feature #101, ver VoyageEmbeddingService). Zero alocação
/// intermediária: os pedaços são ReadOnlyMemory&lt;char&gt; (ponteiro+offset+length) sobre o texto
/// original, não substrings.
/// </summary>
public static class TextChunker
{
    public static List<ReadOnlyMemory<char>> Dividir(string texto, int chunkSize, int chunkOverlap)
    {
        var chunks = new List<ReadOnlyMemory<char>>();
        var mem    = texto.AsMemory();
        int start  = 0;

        while (start < texto.Length)
        {
            int end = start + chunkSize;

            if (end < texto.Length)
                end = EncontrarBoaQuebra(texto, end);
            else
                end = texto.Length;

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
            // EncontrarBoaQuebra recua muito e end - chunkOverlap <= start anterior.
            int nextStart = end - chunkOverlap;
            start = nextStart > start ? nextStart : end;
            if (start >= texto.Length) break;
        }

        return chunks;
    }

    // Opera sobre Span — sem alocar substring temporária.
    private static int EncontrarBoaQuebra(string texto, int around)
    {
        int sliceStart = Math.Max(0, around - 200);
        int sliceEnd   = Math.Min(texto.Length, around + 200);
        var span       = texto.AsSpan(sliceStart, sliceEnd - sliceStart);
        int offset     = sliceStart;

        int paraBreak = span.LastIndexOf("\n\n".AsSpan(), StringComparison.Ordinal);
        if (paraBreak != -1) return offset + paraBreak + 2;

        int lineBreak = span.LastIndexOf('\n');
        if (lineBreak != -1) return offset + lineBreak + 1;

        // Regex ainda precisa de string — mas só é chamada como fallback raro
        var sentenceMatch = System.Text.RegularExpressions.Regex.Match(span.ToString(), @"[.!?]\s");
        if (sentenceMatch.Success) return offset + sentenceMatch.Index + 2;

        return around;
    }
}
