/**
 * Edge Function: buscar-similar
 *
 * Recebe a pergunta do aluno, gera o embedding, busca os chunks mais
 * relevantes (possivelmente de vídeos diferentes) e retorna:
 *   - os itens usados (chunks brutos ou resumos, quando uma aula domina o resultado)
 *   - um campo `contexto_formatado` pronto para injetar no prompt do Claude
 *
 * Diversidade: se uma aula específica ocupar mais de MAX_CHUNKS_POR_AULA posições
 * entre os candidatos, os chunks dela são substituídos por UMA referência ao resumo
 * executivo daquela aula (já gerado e curado) — libera espaço para aulas diferentes
 * no resultado final, em vez de repetir o mesmo trecho ruidoso várias vezes.
 *
 * POST /functions/v1/buscar-similar
 * Body:
 *   {
 *     pergunta:   string,       // dúvida do aluno
 *     area?:      string,       // filtra por área teológica (opcional)
 *     limite?:    number,       // max itens no resultado final (padrão: 6)
 *     threshold?: number,       // similaridade mínima (padrão: 0.35)
 *     canal_id?:  string        // restringe a busca a um canal do YouTube (opcional;
 *                                // omitido = sem restrição, busca em todos os canais)
 *   }
 *
 * Response:
 *   {
 *     chunks: ContextoItem[],
 *     contexto_formatado: string   // pronto para {contexto} no prompt do Claude
 *   }
 */

import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2";

const OPENAI_API_KEY    = Deno.env.get("OPENAI_API_KEY")!;
const SUPABASE_URL      = Deno.env.get("SUPABASE_URL")!;
const SUPABASE_ANON_KEY = Deno.env.get("SUPABASE_ANON_KEY")!;
const EMBED_MODEL       = "text-embedding-3-small";
const MAX_CHUNKS_POR_AULA = 2;

interface ChunkResult {
  video_id:     string;
  titulo:       string;
  chunk_index:  number;
  conteudo:     string;
  area:         string | null;
  subcategoria: string | null;
  similarity:   number;
}

interface ContextoItem {
  tipo:         "chunk" | "resumo";
  video_id:     string;
  titulo:       string;
  area:         string | null;
  chunk_index:  number;
  conteudo:     string;
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
    canal_id?: string;
  };

  try {
    body = await req.json();
  } catch {
    return json({ error: "JSON inválido" }, 400);
  }

  const { pergunta, area, limite = 6, threshold = 0.35, canal_id } = body;
  if (!pergunta?.trim()) {
    return json({ error: "pergunta é obrigatória" }, 400);
  }

  // 1. Gera embedding da pergunta do aluno
  const embedding = await generateEmbedding(pergunta);

  // 2. Busca candidatos via RPC PostgreSQL — híbrida (vetor + full-text via Reciprocal Rank
  //    Fusion). Pede mais candidatos do que o limite final para ter margem de sobra ao
  //    aplicar diversidade por aula.
  const supabase = createClient(SUPABASE_URL, SUPABASE_ANON_KEY);
  const candidatosLimite = Math.min(limite * 4, 24);

  const rpcName   = area ? "match_chunks_hibrido_por_area" : "match_chunks_hibrido";
  const rpcParams = area
    ? { query_embedding: embedding, query_texto: pergunta, area_filtro: area, match_count: candidatosLimite, ...(canal_id ? { canal_id_filtro: canal_id } : {}) }
    : { query_embedding: embedding, query_texto: pergunta, match_count: candidatosLimite, ...(canal_id ? { canal_id_filtro: canal_id } : {}) };

  const { data, error } = await supabase.rpc(rpcName, rpcParams);

  if (error) {
    console.error("RPC error:", error);
    return json({ error: error.message }, 500);
  }

  const candidatos: ChunkResult[] = (data ?? []).filter((c: ChunkResult) => c.similarity >= threshold);

  // 3. Diversidade: no máximo MAX_CHUNKS_POR_AULA chunks por vídeo — o excesso vira
  //    uma única referência ao resumo executivo daquela aula.
  const porVideo = new Map<string, ChunkResult[]>();
  for (const c of candidatos) {
    const lista = porVideo.get(c.video_id) ?? [];
    lista.push(c);
    porVideo.set(c.video_id, lista);
  }

  const itens: ContextoItem[] = [];
  const resumosUsados = new Set<string>();

  for (const c of candidatos) {
    if (itens.length >= limite) break;

    const totalDoVideo = porVideo.get(c.video_id)!.length;
    if (totalDoVideo > MAX_CHUNKS_POR_AULA) {
      if (resumosUsados.has(c.video_id)) continue; // já substituído — pula os demais chunks dessa aula

      resumosUsados.add(c.video_id);
      const resumo = await buscarResumo(supabase, c.video_id);

      if (resumo) {
        itens.push({
          tipo: "resumo", video_id: c.video_id, titulo: c.titulo, area: c.area,
          chunk_index: -1, conteudo: resumo, similarity: c.similarity,
        });
        continue;
      }
      // sem resumo disponível para essa aula: mantém o chunk normalmente
    }

    itens.push({ tipo: "chunk", ...c });
  }

  // 4. Monta o contexto formatado para o prompt do Claude
  const contexto_formatado = formatarContexto(itens);

  return json({ chunks: itens, contexto_formatado });
});

// =========================================================
// Busca o resumo executivo já gerado para o vídeo (substitui múltiplos chunks
// ruidosos por uma única referência curada e do tamanho certo).
// =========================================================

async function buscarResumo(supabase: ReturnType<typeof createClient>, videoId: string): Promise<string | null> {
  const { data, error } = await supabase
    .from("resumo")
    .select("descricao")
    .eq("video_id", videoId)
    .limit(1)
    .maybeSingle();

  if (error || !data) return null;
  return data.descricao as string;
}

// =========================================================
// Formata os itens (chunks ou resumos) num bloco de texto para o prompt do Claude
//
// Exemplo de saída:
//
//   [AULA: Soteriologia — Justificação pela Fé]
//   (Trecho 3 de 18 | Relevância: 87%)
//   "...texto do chunk..."
//
//   [RESUMO DA AULA: Instituto Teológico Giom | 20/04/2026 — Escatologia]
//   "...resumo executivo de 5 minutos já gerado para essa aula..."
// =========================================================

function formatarContexto(itens: ContextoItem[]): string {
  if (itens.length === 0) {
    return "Nenhuma aula relevante encontrada para esta pergunta.";
  }

  return itens
    .map((it) => {
      const areaTag = it.area ? ` — ${it.area}` : "";

      if (it.tipo === "resumo") {
        return `[RESUMO DA AULA: ${it.titulo}${areaTag}]\n"${it.conteudo.trim()}"`;
      }

      const sim = Math.round(it.similarity * 100);
      return [
        `[AULA: ${it.titulo}${areaTag}]`,
        `(Trecho ${it.chunk_index + 1} | Relevância: ${sim}%)`,
        `"${it.conteudo.trim()}"`,
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
