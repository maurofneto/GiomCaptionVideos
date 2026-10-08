import { NextResponse, type NextRequest } from "next/server";
import { randomToken } from "@/lib/crypto";
import { loadAuthEnv } from "@/lib/env";
import { authorize } from "@/lib/session";

// Únicas rotas sem sessão: o início do sign-in e o retorno dele (o callback aplica a allowlist e
// responde 403 a qualquer outro e-mail). Todo o resto exige sessão + allowlist — inclusive
// /api/youtube/callback e as APIs. Os handlers repetem a checagem (defesa em profundidade).
const PUBLIC_PATHS = new Set(["/api/auth/login", "/api/auth/callback"]);

export function buildCsp(nonce: string): string {
  return [
    "default-src 'self'",
    `script-src 'self' 'nonce-${nonce}' 'strict-dynamic'`,
    // Next injeta <style> inline no SSR; sem scripts de terceiros, o risco residual é baixo.
    "style-src 'self' 'unsafe-inline'",
    "img-src 'self' data:",
    "font-src 'self'",
    "connect-src 'self'",
    "object-src 'none'",
    "base-uri 'none'",
    "frame-ancestors 'none'",
    // O POST de "Conectar YouTube" redireciona para o Google; Chrome valida a cadeia.
    "form-action 'self' https://accounts.google.com",
  ].join("; ");
}

function deny(req: NextRequest, csp: string, status: 403 | 503): NextResponse {
  const wantsHtml = req.headers.get("sec-fetch-dest") === "document" || req.headers.get("accept")?.includes("text/html");
  if (status === 403 && wantsHtml && req.method === "GET") {
    // Navegação sem sessão → manda para o sign-in. (Sessão válida porém fora da allowlist cai no 403 abaixo.)
    return NextResponse.redirect(new URL("/api/auth/login", req.url), 303);
  }
  const res = new NextResponse(status === 403 ? "Acesso negado" : "Serviço indisponível", { status });
  res.headers.set("Content-Security-Policy", csp);
  res.headers.set("Cache-Control", "no-store");
  return res;
}

export async function middleware(req: NextRequest): Promise<NextResponse> {
  const nonce = randomToken(16);
  const csp = buildCsp(nonce);

  if (!PUBLIC_PATHS.has(req.nextUrl.pathname)) {
    let env;
    try {
      env = loadAuthEnv();
    } catch {
      return deny(req, csp, 503);
    }
    const auth = await authorize(req.headers.get("cookie"), env);
    if (!auth.ok) {
      if (auth.reason === "forbidden") {
        const res = new NextResponse("Acesso negado", { status: 403 });
        res.headers.set("Content-Security-Policy", csp);
        res.headers.set("Cache-Control", "no-store");
        return res;
      }
      return deny(req, csp, 403);
    }
  }

  const headers = new Headers(req.headers);
  headers.set("x-nonce", nonce);
  headers.set("content-security-policy", csp);
  const res = NextResponse.next({ request: { headers } });
  res.headers.set("Content-Security-Policy", csp);
  return res;
}

export const config = {
  matcher: ["/((?!_next/static|_next/image).*)"],
};
