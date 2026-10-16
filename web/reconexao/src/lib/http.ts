// Helpers de cookie/resposta sobre Request/Response padrão (handlers testáveis sem Next).

export interface CookieOptions {
  maxAge: number;
}

export function parseCookies(header: string | null | undefined): Record<string, string> {
  const out: Record<string, string> = {};
  for (const part of (header ?? "").split(";")) {
    const i = part.indexOf("=");
    if (i < 0) continue;
    const name = part.slice(0, i).trim();
    if (name && !(name in out)) out[name] = part.slice(i + 1).trim();
  }
  return out;
}

/** httpOnly + Secure + SameSite=Lax + Path=/ (prefixo __Host- nos nomes pede exatamente isso). */
export function setCookieHeader(name: string, value: string, opts: CookieOptions): string {
  return `${name}=${value}; Max-Age=${opts.maxAge}; Path=/; HttpOnly; Secure; SameSite=Lax`;
}

export function clearCookieHeader(name: string): string {
  return setCookieHeader(name, "", { maxAge: 0 });
}

const NO_STORE = { "Cache-Control": "no-store, max-age=0" };

export function forbidden(): Response {
  return new Response("Acesso negado", { status: 403, headers: { ...NO_STORE, "Content-Type": "text/plain; charset=utf-8" } });
}

export function badRequest(message: string): Response {
  return new Response(message, { status: 400, headers: { ...NO_STORE, "Content-Type": "text/plain; charset=utf-8" } });
}

/** Redirect 303 para `path` (relativo à origem pública), com cookies opcionais. */
export function redirectTo(origin: string, path: string, setCookies: string[] = []): Response {
  const headers = new Headers({ Location: new URL(path, origin).toString(), ...NO_STORE });
  for (const c of setCookies) headers.append("Set-Cookie", c);
  return new Response(null, { status: 303, headers });
}

/** POSTs de formulário só valem com Origin igual à origem pública (CSRF, além de SameSite=Lax). */
export function originMatches(req: Request, publicOrigin: string): boolean {
  return req.headers.get("origin") === publicOrigin;
}
