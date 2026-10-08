import { headers } from "next/headers";
import { listCanais, type Canal } from "@/lib/canais";
import { getEnv } from "@/lib/env";
import { ageDays, estimateExpiry, WARN_HOURS } from "@/lib/expiry";
import { log } from "@/lib/log";
import {
  mensagemResultado,
  mensagemTeste,
  RESULTADOS,
  REVOGACOES,
  TESTES,
  type Resultado,
  type Revogacao,
} from "@/lib/messages";
import { describeError } from "@/lib/redact";
import { isValidSecretsKey } from "@/lib/secrets-key";
import { authorize } from "@/lib/session";
import { getParamStatus, type ParamStatus } from "@/lib/ssm";

export const dynamic = "force-dynamic";

type Search = Record<string, string | string[] | undefined>;

function one(v: string | string[] | undefined): string | undefined {
  return Array.isArray(v) ? v[0] : v;
}

function num(v: string | string[] | undefined): number | undefined {
  const n = Number(one(v));
  return Number.isFinite(n) ? n : undefined;
}

async function safeStatus(secretsKey: string, kind: "refreshToken" | "cookies"): Promise<ParamStatus | "erro"> {
  try {
    return await getParamStatus(secretsKey, kind);
  } catch (err) {
    log("error", "status_ssm_falhou", { canal: secretsKey, kind, erro: describeError(err) });
    return "erro";
  }
}

const fmt = (d?: Date): string => (d ? d.toLocaleString("pt-BR", { timeZone: "America/Sao_Paulo" }) : "—");

export default async function Page({ searchParams }: { searchParams: Promise<Search> }) {
  // Defesa em profundidade: o middleware já barrou, mas a página também confere.
  const env = getEnv();
  const auth = await authorize((await headers()).get("cookie"), env);
  if (!auth.ok) return <main><p>Acesso negado.</p></main>;

  const sp = await searchParams;

  let canais: Canal[] = [];
  let erroLista = false;
  try {
    canais = await listCanais();
  } catch (err) {
    erroLista = true;
    log("error", "lista_canais_falhou", { erro: describeError(err) });
  }

  const statuses = await Promise.all(
    canais.map(async (c) => ({
      refresh: await safeStatus(c.secretsKey, "refreshToken"),
      cookies: await safeStatus(c.secretsKey, "cookies"),
    })),
  );

  const res = one(sp.res);
  const resultado = (RESULTADOS as readonly string[]).includes(res ?? "") ? (res as Resultado) : undefined;
  const canalParam = one(sp.canal);
  const canalAlvo = isValidSecretsKey(canalParam) ? canais.find((c) => c.secretsKey === canalParam) : undefined;
  const teste = (TESTES as readonly string[]).includes(one(sp.teste) ?? "") ? (one(sp.teste) as (typeof TESTES)[number]) : undefined;
  const revParam = one(sp.rev);
  const revogacao = (REVOGACOES as readonly string[]).includes(revParam ?? "") ? (revParam as Revogacao) : undefined;
  const banner = resultado
    ? mensagemResultado(resultado, canalAlvo?.nome ?? canalParam ?? "—", {
        revogacao,
        linha: num(sp.linha),
        bytes: num(sp.bytes),
        limite: num(sp.limite),
        mantidos: num(sp.mantidos),
      })
    : undefined;
  const bannerTeste = teste ? mensagemTeste(teste) : undefined;

  return (
    <main>
      <header>
        <div>
          <h1>Reconexão de canais</h1>
          <p className="meta">
            {auth.session.email} · app OAuth em modo <strong>{env.oauthAppMode}</strong> · prefixo SSM {env.ssmPrefix}
          </p>
        </div>
        <form action="/api/auth/logout" method="post">
          <button type="submit">Sair</button>
        </form>
      </header>

      {banner && <div className={`banner ${banner.tone}`} role="status">{banner.text}</div>}
      {bannerTeste && <div className={`banner ${bannerTeste.tone}`} role="status">{bannerTeste.text}</div>}
      {erroLista && <div className="banner erro">Não foi possível ler a lista de canais. Verifique a configuração da fonte de canais.</div>}
      {!erroLista && canais.length === 0 && <p>Nenhum canal ativo em canal_ingestao.</p>}

      {canais.map((c, i) => {
        const st = statuses[i];
        const refresh = st?.refresh;
        const cookies = st?.cookies;
        const connectedAt = refresh && refresh !== "erro" ? refresh.lastModified : undefined;
        const exp = estimateExpiry(env.oauthAppMode, env.oauthTestingExpiryDays, connectedAt);
        const cookieAge = cookies && cookies !== "erro" ? ageDays(cookies.lastModified) : undefined;
        const ultimo = canalAlvo?.secretsKey === c.secretsKey ? (bannerTeste ?? banner) : undefined;

        return (
          <section key={c.secretsKey} className={`card ${exp.level === "warn" || exp.level === "expired" ? "destaque" : ""}`}>
            <h2>{c.nome}</h2>
            <p className="meta">
              channel_id {c.channelId} · secrets_key {c.secretsKey}
            </p>

            <div className="grid">
              <div>
                <h3>YouTube (OAuth)</h3>
                {refresh === "erro" && <p>Status indisponível (erro ao consultar SSM).</p>}
                {refresh && refresh !== "erro" && !refresh.exists && <p>Não conectado.</p>}
                {refresh && refresh !== "erro" && refresh.exists && (
                  <ul>
                    <li>Conectado em: {fmt(refresh.lastModified)} (versão {refresh.version})</li>
                    {exp.level !== "none" && (
                      <li>
                        Expira (estimado, modo Testing): {fmt(exp.expiresAt)}
                        {exp.level === "expired" && <strong> — provavelmente já expirou</strong>}
                        {exp.level === "warn" && <strong> — faltam ≤ {WARN_HOURS} h ({exp.hoursLeft} h)</strong>}
                      </li>
                    )}
                  </ul>
                )}
                {ultimo && <p className="meta">Última tentativa: {ultimo.text}</p>}
                <form action="/api/youtube/connect" method="post">
                  <input type="hidden" name="secretsKey" value={c.secretsKey} />
                  <button type="submit">Conectar YouTube</button>
                </form>
                <p className="meta">
                  {env.revokeOnMismatch
                    ? "Atenção: entrar com a conta de outro canal pode revogar a autorização dessa conta para este app."
                    : "Dica: entre com a conta dona deste canal."}
                </p>
              </div>

              <div>
                <h3>Cookies yt-dlp (fallback)</h3>
                {cookies === "erro" && <p>Status indisponível (erro ao consultar SSM).</p>}
                {cookies && cookies !== "erro" && !cookies.exists && <p>Sem cookies gravados.</p>}
                {cookies && cookies !== "erro" && cookies.exists && (
                  <ul>
                    <li>
                      Atualizados em: {fmt(cookies.lastModified)} ({cookieAge} dia(s) atrás, versão {cookies.version})
                    </li>
                    <li>Tamanho (cifrado, aprox.): {cookies.encryptedBytes ?? "—"} bytes</li>
                  </ul>
                )}
                <details>
                  <summary>Atualizar cookies</summary>
                  <form action="/api/cookies" method="post" encType="multipart/form-data">
                    <input type="hidden" name="secretsKey" value={c.secretsKey} />
                    <p>
                      <input type="file" name="arquivo" accept=".txt,text/plain" />
                    </p>
                    <p>
                      <textarea name="texto" placeholder="# Netscape HTTP Cookie File ..." spellCheck={false} autoComplete="off" />
                    </p>
                    <button type="submit">Atualizar cookies</button>
                  </form>
                </details>
              </div>
            </div>
          </section>
        );
      })}
    </main>
  );
}
