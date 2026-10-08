import { getEnv, type Env } from "./env";
import { forbidden, originMatches } from "./http";
import { log } from "./log";
import { authorize, type Session } from "./session";

export interface Access {
  session: Session;
  env: Env;
}

/**
 * Checagem de acesso de CADA handler (defesa em profundidade além do proxy). Qualquer falha
 * — sem sessão, e-mail fora da allowlist, Origin errada em POST — vira 403 sem detalhe.
 */
export async function requireAccess(req: Request, opts: { checkOrigin?: boolean } = {}): Promise<Access | Response> {
  let env: Env;
  try {
    env = getEnv();
  } catch {
    // Configuração inválida: falha fechada. Detalhe (nomes das variáveis) já sai no boot.
    return new Response("Serviço indisponível", { status: 503, headers: { "Cache-Control": "no-store" } });
  }
  const auth = await authorize(req.headers.get("cookie"), env);
  if (!auth.ok) {
    log("warn", "acesso_negado", { motivo: auth.reason, rota: new URL(req.url).pathname });
    return forbidden();
  }
  if (opts.checkOrigin && !originMatches(req, env.publicOrigin)) {
    log("warn", "origin_invalida", { rota: new URL(req.url).pathname });
    return forbidden();
  }
  return { session: auth.session, env };
}
