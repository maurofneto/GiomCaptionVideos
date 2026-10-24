import { NextResponse, type NextRequest } from "next/server";
import { randomToken } from "@/lib/crypto";
import { loadAuthEnv } from "@/lib/env";
import { authorize } from "@/lib/session";

// Únicas rotas sem sessão: o início do sign-in e o retorno dele (o callback aplica a allowlist e
// responde 403 a qualquer outro e-mail), o logout, público de propósito (idempotente: só limpa
// cookies; o CSRF é checado pela Origin no handler) e as páginas legais estáticas que o Google
// exige para publicar o app OAuth (/sobre, /privacidade, /termos). Todo o resto exige sessão + allowlist —
// inclusive /api/youtube/callback e as APIs. Os handlers repetem a checagem (defesa em profundidade).
const PUBLIC_PATHS = new Set([
  "/api/auth/login",
  "/api/auth/callback",
  "/api/auth/logout",
  // Páginas legais exigidas pelo Google (verificação do app OAuth): estáticas, sem dado sensível.
  "/sobre",
  "/privacidade",
  "/termos",
  // Arquivo de verificação de propriedade do Google Search Console (conteúdo fixo, sem dado sensível).
  "/google538c20e4d5c5d8f9.html",
]);

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

function wantsHtml(req: NextRequest): boolean {
  return req.headers.get("sec-fetch-dest") === "document" || !!req.headers.get("accept")?.includes("text/html");
}

// 403 de GET de documento com sessão válida fora da allowlist: dá uma saída (Sair → escolher outra conta).
// Sem script inline, sem e-mail nem dado de sessão; o POST do form passa pela checagem de Origin do handler.
const FORBIDDEN_HTML = `<!doctype html><html lang="pt-BR"><head><meta charset="utf-8"><title>Acesso negado</title></head><body><p>Acesso negado</p><form method="post" action="/api/auth/logout"><button type="submit">Sair</button></form></body></html>`;

function deny(req: NextRequest, csp: string, status: 403 | 503): NextResponse {
  if (status === 403 && wantsHtml(req) && req.method === "GET") {
    // Navegação sem sessão → manda para o sign-in. (Sessão válida porém fora da allowlist cai no 403 abaixo.)
    return NextResponse.redirect(new URL("/api/auth/login", req.url), 303);
  }
  const res = new NextResponse(status === 403 ? "Acesso negado" : "Serviço indisponível", { status });
  res.headers.set("Content-Security-Policy", csp);
  res.headers.set("Cache-Control", "no-store");
  return res;
}

export async function proxy(req: NextRequest): Promise<NextResponse> {
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
        const html = req.method === "GET" && wantsHtml(req);
        const res = new NextResponse(html ? FORBIDDEN_HTML : "Acesso negado", { status: 403 });
        if (html) res.headers.set("Content-Type", "text/html; charset=utf-8");
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
