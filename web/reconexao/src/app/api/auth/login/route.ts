import { randomToken, sealPayload, sha256B64url } from "@/lib/crypto";
import { getEnv } from "@/lib/env";
import { buildLoginAuthUrl } from "@/lib/google";
import { setCookieHeader } from "@/lib/http";
import { LOGIN_COOKIE, LOGIN_LABEL, LOGIN_TTL_SECONDS, type LoginState } from "@/lib/login-cookie";

export const dynamic = "force-dynamic";

// Sign-in separado: client OAuth próprio, escopos só "openid email". Rota pública por
// necessidade (é a porta de entrada); quem decide o acesso é o callback + allowlist.
export async function GET(): Promise<Response> {
  const env = getEnv();
  const verifier = randomToken(48);
  const payload: LoginState = {
    state: randomToken(32),
    nonce: randomToken(16),
    verifier,
    exp: Math.floor(Date.now() / 1000) + LOGIN_TTL_SECONDS,
  };
  const cookie = await sealPayload(env.sessionSecret, LOGIN_LABEL, payload);
  const url = buildLoginAuthUrl(
    { clientId: env.authClientId, clientSecret: env.authClientSecret, redirectUri: env.authRedirectUri },
    payload.state,
    payload.nonce,
    await sha256B64url(verifier),
  );
  return new Response(null, {
    status: 303,
    headers: {
      Location: url,
      "Cache-Control": "no-store",
      "Set-Cookie": setCookieHeader(LOGIN_COOKIE, cookie, { maxAge: LOGIN_TTL_SECONDS }),
    },
  });
}
