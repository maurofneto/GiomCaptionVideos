import { getEnv, type Env } from "@/lib/env";
import { clearCookieHeader, forbidden, originMatches, redirectTo } from "@/lib/http";
import { log } from "@/lib/log";
import { LOGIN_COOKIE } from "@/lib/login-cookie";
import { SESSION_COOKIE } from "@/lib/session";

export const dynamic = "force-dynamic";

// Público no proxy de propósito: o logout é idempotente (não exige sessão, só limpa cookies) e o
// CSRF é checado aqui, pela Origin, ANTES de qualquer Set-Cookie. A sessão é um cookie assinado
// stateless — não há revogação server-side a fazer.
export async function POST(req: Request): Promise<Response> {
  let env: Env;
  try {
    env = getEnv();
  } catch {
    return new Response("Serviço indisponível", { status: 503, headers: { "Cache-Control": "no-store" } });
  }
  if (!originMatches(req, env.publicOrigin)) {
    log("warn", "origin_invalida", { rota: new URL(req.url).pathname });
    return forbidden();
  }
  return redirectTo(env.publicOrigin, "/api/auth/login", [
    clearCookieHeader(SESSION_COOKIE),
    clearCookieHeader(LOGIN_COOKIE),
  ]);
}

function methodNotAllowed(): Response {
  return new Response("Método não permitido", {
    status: 405,
    headers: { Allow: "POST", "Cache-Control": "no-store", "Content-Type": "text/plain; charset=utf-8" },
  });
}

export const GET = methodNotAllowed;
export const HEAD = methodNotAllowed;
export const PUT = methodNotAllowed;
export const PATCH = methodNotAllowed;
export const DELETE = methodNotAllowed;
export const OPTIONS = methodNotAllowed;
