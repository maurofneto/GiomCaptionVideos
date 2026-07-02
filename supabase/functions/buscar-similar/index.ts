/**
 * Edge Function: buscar-similar
 *
 * Recebe a pergunta do aluno, gera o embedding, busca os chunks mais
 * relevantes (possivelmente de vídeos diferentes) e retorna:
 *   - os chunks brutos (para o agent montar o prompt)
 *   - um campo `contexto_formatado` pronto para injetar no prompt do Claude
 *
 * POST /functions/v1/buscar-similar
 * Body:
 *   {
 *     pergunta:   string,       // dúvida do aluno
 *     area?:      string,       // filtra por área teológica (opcional)
 *     limite?:    number,       // max chunks retornados (padrão: 6)
 *     threshold?: number        // similaridade mínima (padrão: 0.70)
 *   }
 *
 * Response:
 *   {
 *     chunks: ChunkResult[],
 *     contexto_formatado: string   // pronto para {contexto} no prompt do Claude
 *   }
 */

import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2";

const OPENAI_API_KEY    = Deno.env.get("OPENAI_API_KEY")!;
const SUPABASE_URL      = Deno.env.get("SUPABASE_URL")!;
const SUPABASE_ANON_KEY = Deno.env.get("SUPABASE_ANON_KEY")!;
const EMBED_MODEL       = "text-embedding-3-small";

interface ChunkResult {
  video_id:     string;
  titulo:       string;
  chunk_index:  number;
  conteudo:     string;
  area:         string | null;
  subcategoria: string | null;
  similarity:   number;
}

serve(async (req) => {
  if (req.method !== "POST") {
    return json({ error: "Method not allowed" }, 405);
  }

  let body: {
    pergunta: string;
    area?: string;
    limite?: number;
    threshold?: number;
  };

  try {
    body = await req.json();
  } catch {
    return json({ error: "JSON inválido" }, 400);
  }

  const { pergunta, area, limite = 6, threshold = 0.70 } = body;
  if (!pergunta?.trim()) {
    return json({ error: "pergunta é obrigatória" }, 400);
  }

  // 1. Gera embedding da pergunta do aluno
  const embedding = await generateEmbedding(pergunta);

  // 2. Busca chunks similares via RPC PostgreSQL
  const supabase = createClient(SUPABASE_URL, SUPABASE_ANON_KEY);

  const rpcName   = area ? "match_chunks_por_area" : "match_chunks";
  const rpcParams = area
    ? { query_embedding: embedding, area_filtro: area, match_threshold: threshold, match_count: limite }
    : { query_embedding: embedding, match_threshold: threshold, match_count: limite };

  const { data, error } = await supabase.rpc(rpcName, rpcParams);

  if (error) {
    console.error("RPC error:", error);
    return json({ error: error.message }, 500);
  }

  const chunks: ChunkResult[] = data ?? [];

  // 3. Monta o contexto formatado para o prompt do Claude
  //    Agrupa por vídeo para melhor leitura pelo modelo
  const contexto_formatado = formatarContexto(chunks);

  return json({ chunks, contexto_formatado });
});

// =========================================================
// Formata os chunks num bloco de texto para o prompt do Claude
//
// Exemplo de saída:
//
//   [AULA: Soteriologia — Justificação pela Fé]
//   (Trecho 3 de 18 | Similaridade: 87%)
//   "...texto do chunk..."
//
//   [AULA: Cristologia — A Pessoa de Cristo]
//   (Trecho 7 de 22 | Similaridade: 81%)
//   "...texto do chunk..."
// =========================================================

function formatarContexto(chunks: ChunkResult[]): string {
  if (chunks.length === 0) {
    return "Nenhuma aula relevante encontrada para esta pergunta.";
  }

  return chunks
    .map((c) => {
      const area = c.area ? ` — ${c.area}` : "";
      const sim  = Math.round(c.similarity * 100);
      return [
        `[AULA: ${c.titulo}${area}]`,
        `(Trecho ${c.chunk_index + 1} | Relevância: ${sim}%)`,
        `"${c.conteudo.trim()}"`,
      ].join("\n");
    })
    .join("\n\n---\n\n");
}

// =========================================================
// Embedding via OpenAI (single input)
// =========================================================

async function generateEmbedding(text: string): Promise<number[]> {
  const response = await fetch("https://api.openai.com/v1/embeddings", {
    method: "POST",
    headers: {
      "Authorization": `Bearer ${OPENAI_API_KEY}`,
      "Content-Type": "application/json",
    },
    body: JSON.stringify({ model: EMBED_MODEL, input: text }),
  });

  if (!response.ok) {
    const err = await response.text();
    throw new Error(`OpenAI embedding falhou: ${err}`);
  }

  const data = await response.json();
  return data.data[0].embedding;
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}
