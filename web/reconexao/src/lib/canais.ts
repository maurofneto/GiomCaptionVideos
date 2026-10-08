import { getEnv } from "./env";
import { log } from "./log";
import { isValidSecretsKey } from "./secrets-key";

export interface Canal {
  channelId: string;
  nome: string;
  secretsKey: string;
}

/** Erro da fonte de canais já reduzido a campos seguros (status), sem URL com query nem token. */
export class FonteCanaisError extends Error {
  constructor(readonly status: number) {
    super(`fonte de canais respondeu HTTP ${status}`);
    this.name = "FonteCanaisError";
  }
}

/**
 * Canais ativos de canal_ingestao via PostgREST (dev: Railway; prod: Supabase), sempre com o JWT
 * da role reconexao_leitura em `Authorization: Bearer` — nunca anônimo, mesmo que a fonte aceite.
 * `apikey` só vai quando CANAIS_API_KEY está definido (gateway do Supabase). Canal novo = linha nova.
 */
export async function listCanais(): Promise<Canal[]> {
  const env = getEnv();
  const url = `${env.canaisApiUrl}${env.canaisApiRestPrefix}/canal_ingestao?select=channel_id,nome,secrets_key&ativo=eq.true&order=nome.asc`;
  const headers: Record<string, string> = { Authorization: `Bearer ${env.canaisApiToken}` };
  if (env.canaisApiKey) headers.apikey = env.canaisApiKey;

  const res = await fetch(url, { headers, cache: "no-store", signal: AbortSignal.timeout(10_000) });
  if (res.status === 401 || res.status === 403) log("error", "fonte_canais_negou", { status: res.status });
  if (!res.ok) throw new FonteCanaisError(res.status);

  const rows = (await res.json()) as Array<{ channel_id?: unknown; nome?: unknown; secrets_key?: unknown }>;
  return rows.flatMap((r) =>
    typeof r.channel_id === "string" && typeof r.nome === "string" && isValidSecretsKey(r.secrets_key)
      ? [{ channelId: r.channel_id, nome: r.nome, secretsKey: r.secrets_key }]
      : [],
  );
}
