import { openPayload, randomToken, sealPayload, sha256B64url, timingSafeEqual } from "./crypto";
import { parseCookies } from "./http";

// Cookie criptografado (AES-GCM) com state + PKCE verifier + canal escolhido + sid da sessão.
// Uso único: o callback sempre manda apagar o cookie, então um segundo uso do mesmo state não
// encontra nada para validar. TTL curto limita a janela.

export const OAUTH_COOKIE = "__Host-rc_oauth";
export const OAUTH_TTL_SECONDS = 10 * 60;
const LABEL = "oauth-state-v1";

export interface OAuthState {
  state: string;
  verifier: string;
  secretsKey: string;
  channelId: string;
  sid: string;
  exp: number;
}

export interface StartedOAuth {
  state: string;
  challenge: string;
  cookieValue: string;
}

export async function startOAuth(
  secret: string,
  input: { secretsKey: string; channelId: string; sid: string },
  now = Date.now(),
): Promise<StartedOAuth> {
  const state = randomToken(32);
  const verifier = randomToken(48);
  const payload: OAuthState = {
    state,
    verifier,
    secretsKey: input.secretsKey,
    channelId: input.channelId,
    sid: input.sid,
    exp: Math.floor(now / 1000) + OAUTH_TTL_SECONDS,
  };
  return {
    state,
    challenge: await sha256B64url(verifier),
    cookieValue: await sealPayload(secret, LABEL, payload),
  };
}

export type ConsumeResult = { ok: true; value: OAuthState } | { ok: false; reason: "missing" | "invalid" | "expired" | "mismatch" };

/** Valida o cookie contra o `state` da query e a sessão. NÃO apaga — o chamador sempre emite Set-Cookie de remoção. */
export async function validateOAuth(
  secret: string,
  cookieHeader: string | null | undefined,
  stateParam: string | null,
  sid: string,
  now = Date.now(),
): Promise<ConsumeResult> {
  const raw = parseCookies(cookieHeader)[OAUTH_COOKIE];
  if (!raw) return { ok: false, reason: "missing" };
  const value = await openPayload<OAuthState>(secret, LABEL, raw);
  if (!value || typeof value.state !== "string") return { ok: false, reason: "invalid" };
  if (value.exp * 1000 < now) return { ok: false, reason: "expired" };
  if (!stateParam || !timingSafeEqual(value.state, stateParam) || !timingSafeEqual(value.sid, sid)) {
    return { ok: false, reason: "mismatch" };
  }
  return { ok: true, value };
}
