import { getEnv } from "./env";
import { isValidSecretsKey } from "./secrets-key";

export interface Canal {
  channelId: string;
  nome: string;
  secretsKey: string;
}

/**
 * Canais ativos de canal_ingestao via PostgREST, com o JWT da role reconexao_leitura (SELECT só
 * em channel_id/nome/secrets_key/ativo — ver migration 20261008120000). Canal novo = linha nova.
 */
export async function listCanais(): Promise<Canal[]> {
  const env = getEnv();
  const url = `${env.supabaseUrl.replace(/\/$/, "")}/rest/v1/canal_ingestao?select=channel_id,nome,secrets_key&ativo=eq.true&order=nome.asc`;
  const res = await fetch(url, {
    headers: { apikey: env.supabaseReconexaoJwt, Authorization: `Bearer ${env.supabaseReconexaoJwt}` },
    cache: "no-store",
    signal: AbortSignal.timeout(10_000),
  });
  if (!res.ok) throw new Error(`Supabase canal_ingestao HTTP ${res.status}`);

  const rows = (await res.json()) as Array<{ channel_id?: unknown; nome?: unknown; secrets_key?: unknown }>;
  return rows.flatMap((r) =>
    typeof r.channel_id === "string" && typeof r.nome === "string" && isValidSecretsKey(r.secrets_key)
      ? [{ channelId: r.channel_id, nome: r.nome, secretsKey: r.secrets_key }]
      : [],
  );
}
