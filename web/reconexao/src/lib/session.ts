import { randomToken, signPayload, verifyPayload } from "./crypto";
import { parseCookies } from "./http";
import type { AuthEnv } from "./env";

export const SESSION_COOKIE = "__Host-rc_session";
export const SESSION_TTL_SECONDS = 8 * 60 * 60;
const LABEL = "session-v1";

export interface Session {
  email: string;
  /** id aleatório da sessão — amarra o state do OAuth do YouTube a esta sessão. */
  sid: string;
  exp: number;
}

export function isAllowed(email: string, allowed: string[]): boolean {
  return allowed.includes(email.trim().toLowerCase());
}

export async function createSessionToken(env: AuthEnv, email: string, now = Date.now()): Promise<string> {
  const session: Session = {
    email: email.trim().toLowerCase(),
    sid: randomToken(16),
    exp: Math.floor(now / 1000) + SESSION_TTL_SECONDS,
  };
  return signPayload(env.sessionSecret, LABEL, session);
}

export type AuthResult = { ok: true; session: Session } | { ok: false; reason: "no-session" | "forbidden" };

/**
 * Sessão válida (assinatura + expiração) E e-mail ainda na allowlist (re-checada a cada
 * requisição: tirar alguém da env var revoga o acesso sem esperar a sessão expirar).
 */
export async function authorize(cookieHeader: string | null | undefined, env: AuthEnv, now = Date.now()): Promise<AuthResult> {
  const token = parseCookies(cookieHeader)[SESSION_COOKIE];
  if (!token) return { ok: false, reason: "no-session" };
  const session = await verifyPayload<Session>(env.sessionSecret, LABEL, token);
  if (!session || typeof session.email !== "string" || session.exp * 1000 < now) {
    return { ok: false, reason: "no-session" };
  }
  return isAllowed(session.email, env.allowedEmails) ? { ok: true, session } : { ok: false, reason: "forbidden" };
}
