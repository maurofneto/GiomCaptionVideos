import { openPayload, timingSafeEqual } from "@/lib/crypto";
import { getEnv } from "@/lib/env";
import { exchangeCode, readIdTokenClaims } from "@/lib/google";
import { clearCookieHeader, forbidden, parseCookies, redirectTo, setCookieHeader } from "@/lib/http";
import { LOGIN_COOKIE, LOGIN_LABEL, type LoginState } from "@/lib/login-cookie";
import { log } from "@/lib/log";
import { describeError } from "@/lib/redact";
import { createSessionToken, isAllowed, SESSION_COOKIE, SESSION_TTL_SECONDS } from "@/lib/session";

export const dynamic = "force-dynamic";

export async function GET(req: Request): Promise<Response> {
  const env = getEnv();
  const clear = clearCookieHeader(LOGIN_COOKIE);
  const deny = (motivo: string): Response => {
    log("warn", "login_negado", { motivo });
    const res = forbidden();
    res.headers.append("Set-Cookie", clear);
    return res;
  };

  const url = new URL(req.url);
  const code = url.searchParams.get("code");
  const state = url.searchParams.get("state");
  const raw = parseCookies(req.headers.get("cookie"))[LOGIN_COOKIE];
  if (!code || !state || !raw) return deny("parametros");

  const saved = await openPayload<LoginState>(env.sessionSecret, LOGIN_LABEL, raw);
  if (!saved || saved.exp * 1000 < Date.now() || !timingSafeEqual(saved.state, state)) return deny("state");

  try {
    const tokens = await exchangeCode(
      { clientId: env.authClientId, clientSecret: env.authClientSecret, redirectUri: env.authRedirectUri },
      code,
      saved.verifier,
    );
    const claims = tokens.idToken ? readIdTokenClaims(tokens.idToken) : null;
    const email = typeof claims?.email === "string" ? claims.email : "";
    const aud = claims?.aud;
    const validClaims =
      !!claims &&
      (aud === env.authClientId || (Array.isArray(aud) && aud.includes(env.authClientId))) &&
      (claims.iss === "https://accounts.google.com" || claims.iss === "accounts.google.com") &&
      typeof claims.exp === "number" &&
      claims.exp * 1000 > Date.now() &&
      claims.nonce === saved.nonce &&
      claims.email_verified === true;
    if (!validClaims || !isAllowed(email, env.allowedEmails)) return deny("claims_ou_allowlist");

    const token = await createSessionToken(env, email);
    log("info", "login_ok", {});
    return redirectTo(env.publicOrigin, "/", [
      clear,
      setCookieHeader(SESSION_COOKIE, token, { maxAge: SESSION_TTL_SECONDS }),
    ]);
  } catch (err) {
    log("error", "login_erro", { erro: describeError(err) });
    return deny("troca");
  }
}
