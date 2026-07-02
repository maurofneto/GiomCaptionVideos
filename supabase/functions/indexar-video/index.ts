/**
 * Edge Function: indexar-video
 *
 * Recebe apenas o video_id, busca a transcrição e o título no banco,
 * divide em chunks sobrepostos, gera embeddings via OpenAI e persiste no pgvector.
 *
 * POST /functions/v1/indexar-video
 * Body: { video_id: string }
 */

import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2";

const OPENAI_API_KEY       = Deno.env.get("OPENAI_API_KEY")!;
const SUPABASE_URL         = Deno.env.get("SUPABASE_URL")!;
const SUPABASE_SERVICE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;

const CHUNK_SIZE    = 2400;
const CHUNK_OVERLAP = 400;
const EMBED_MODEL   = "text-embedding-3-small";
const EMBED_BATCH   = 20;

serve(async (req) => {
  if (req.method !== "POST") {
    return json({ error: "Method not allowed" }, 405);
  }

  let body: { video_id: string };
  try {
    body = await req.json();
  } catch {
    return json({ error: "JSON inválido" }, 400);
  }

  const { video_id } = body;
  if (!video_id) {
    return json({ error: "video_id é obrigatório" }, 400);
  }

  const supabase = createClient(SUPABASE_URL, SUPABASE_SERVICE_KEY);

  // 1. Busca título do vídeo
  const { data: videoData, error: videoError } = await supabase
    .from("video")
    .select("title")
    .eq("id", video_id)
    .single();

  if (videoError || !videoData) {
    console.error("Vídeo não encontrado:", videoError);
    return json({ error: "Vídeo não encontrado" }, 404);
  }

  // 2. Busca transcrição
  const { data: transcricaoData, error: transcricaoError } = await supabase
    .from("transcricao")
    .select("conteudo")
    .eq("video_id", video_id)
    .single();

  if (transcricaoError || !transcricaoData?.conteudo) {
    console.error("Transcrição não encontrada:", transcricaoError);
    return json({ error: "Transcrição não encontrada para este vídeo" }, 404);
  }

  const titulo      = videoData.title as string;
  const transcricao = transcricaoData.conteudo as string;

  console.log(`[${video_id}] "${titulo}" | ${transcricao.length} chars`);

  // 3. Divide em chunks sobrepostos
  const chunks = splitIntoChunks(transcricao, CHUNK_SIZE, CHUNK_OVERLAP);
  console.log(`[${video_id}] ${chunks.length} chunks gerados`);

  // 4. Gera embeddings em lotes
  const embeddings: number[][] = [];
  for (let i = 0; i < chunks.length; i += EMBED_BATCH) {
    const batch = chunks.slice(i, i + EMBED_BATCH);
    const batchEmbeddings = await generateEmbeddings(batch);
    embeddings.push(...batchEmbeddings);

    if (i + EMBED_BATCH < chunks.length) {
      await sleep(200);
    }
  }

  // 5. Apaga chunks antigos e insere os novos
  const { error: deleteError } = await supabase
    .from("video_chunk")
    .delete()
    .eq("video_id", video_id);

  if (deleteError) {
    console.error("Erro ao deletar chunks antigos:", deleteError);
    return json({ error: deleteError.message }, 500);
  }

  const rows = chunks.map((conteudo, index) => ({
    video_id,
    titulo,
    chunk_index: index,
    conteudo,
    embedding: embeddings[index],
  }));

  for (let i = 0; i < rows.length; i += 50) {
    const { error: insertError } = await supabase
      .from("video_chunk")
      .insert(rows.slice(i, i + 50));

    if (insertError) {
      console.error(`Erro ao inserir chunks [${i}..${i + 50}]:`, insertError);
      return json({ error: insertError.message }, 500);
    }
  }

  console.log(`[${video_id}] ${chunks.length} chunks indexados com sucesso`);
  return json({ success: true, video_id, chunks_indexados: chunks.length });
});

// =========================================================
// Chunking
// =========================================================

function splitIntoChunks(text: string, size: number, overlap: number): string[] {
  const chunks: string[] = [];
  let start = 0;

  while (start < text.length) {
    let end = start + size;

    if (end < text.length) {
      end = findGoodBreak(text, end);
    } else {
      end = text.length;
    }

    const chunk = text.slice(start, end).trim();
    if (chunk.length > 0) chunks.push(chunk);

    start = end - overlap;
    if (start >= text.length) break;
  }

  return chunks;
}

function findGoodBreak(text: string, around: number): number {
  const window = 200;
  const slice = text.slice(Math.max(0, around - window), around + window);

  const paraBreak = slice.lastIndexOf("\n\n");
  if (paraBreak !== -1) return Math.max(0, around - window) + paraBreak + 2;

  const lineBreak = slice.lastIndexOf("\n");
  if (lineBreak !== -1) return Math.max(0, around - window) + lineBreak + 1;

  const sentenceBreak = slice.search(/[.!?]\s/);
  if (sentenceBreak !== -1) return Math.max(0, around - window) + sentenceBreak + 2;

  return around;
}

// =========================================================
// Embeddings via OpenAI (batch)
// =========================================================

async function generateEmbeddings(texts: string[]): Promise<number[][]> {
  const response = await fetch("https://api.openai.com/v1/embeddings", {
    method: "POST",
    headers: {
      "Authorization": `Bearer ${OPENAI_API_KEY}`,
      "Content-Type": "application/json",
    },
    body: JSON.stringify({ model: EMBED_MODEL, input: texts }),
  });

  if (!response.ok) {
    const err = await response.text();
    throw new Error(`OpenAI embeddings falhou: ${err}`);
  }

  const data = await response.json();
  return data.data
    .sort((a: { index: number }, b: { index: number }) => a.index - b.index)
    .map((item: { embedding: number[] }) => item.embedding);
}

// =========================================================
// Helpers
// =========================================================

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
