import { beforeEach, describe, expect, it, vi } from "vitest";
import { b64urlEncode, sealPayload } from "@/lib/crypto";
import { LOGIN_COOKIE, LOGIN_LABEL } from "@/lib/login-cookie";
import { GOOGLE, mockFetch, ORIGIN } from "./helpers";
import { TEST_ENV } from "./test-env";

import { GET as loginCallback } from "@/app/api/auth/callback/route";
import { GET as loginStart } from "@/app/api/auth/login/route";

const enc = (o: unknown) => b64urlEncode(new TextEncoder().encode(JSON.stringify(o)));
const idToken = (claims: Record<string, unknown>) => `${enc({ alg: "RS256" })}.${enc(claims)}.sig`;

async function loginCookie(overrides: { exp?: number } = {}) {
  const payload = { state: "st-1", nonce: "nonce-1", verifier: "ver-1", exp: overrides.exp ?? Math.floor(Date.now() / 1000) + 300 };
  return `${LOGIN_COOKIE}=${await sealPayload(TEST_ENV.SESSION_SECRET as string, LOGIN_LABEL, payload)}`;
}

const goodClaims = (email: string) => ({
  iss: "https://accounts.google.com",
  aud: TEST_ENV.AUTH_GOOGLE_CLIENT_ID,
  exp: Math.floor(Date.now() / 1000) + 300,
  nonce: "nonce-1",
  email,
  email_verified: true,
});

beforeEach(() => {
  vi.spyOn(console, "warn").mockImplementation(() => undefined);
  vi.spyOn(console, "info").mockImplementation(() => undefined);
});

describe("sign-in separado (openid email)", () => {
  it("usa client próprio e só os escopos openid email, com PKCE", async () => {
    const res = await loginStart();
    const p = new URL(res.headers.get("location") as string).searchParams;
    expect(p.get("scope")).toBe("openid email");
    expect(p.get("client_id")).toBe(TEST_ENV.AUTH_GOOGLE_CLIENT_ID);
    expect(p.get("code_challenge_method")).toBe("S256");
    expect(res.headers.get("set-cookie")).toMatch(/HttpOnly.*Secure|Secure.*HttpOnly/);
  });

  it("e-mail da allowlist recebe sessão httpOnly", async () => {
    mockFetch({ [GOOGLE.token]: { json: { access_token: "a", id_token: idToken(goodClaims("Mauro@example.test")) } } });
    const res = await loginCallback(new Request(`${ORIGIN}/api/auth/callback?code=c&state=st-1`, { headers: { cookie: await loginCookie() } }));
    expect(res.status).toBe(303);
    const cookies = res.headers.getSetCookie().join("\n");
    expect(cookies).toMatch(/__Host-rc_session=.+HttpOnly/);
  });

  it("e-mail fora da allowlist recebe 403 e nenhuma sessão", async () => {
    mockFetch({ [GOOGLE.token]: { json: { access_token: "a", id_token: idToken(goodClaims("intruso@example.test")) } } });
    const res = await loginCallback(new Request(`${ORIGIN}/api/auth/callback?code=c&state=st-1`, { headers: { cookie: await loginCookie() } }));
    expect(res.status).toBe(403);
    expect(res.headers.getSetCookie().join("\n")).not.toContain("rc_session=ey");
  });

  it.each([
    ["state errado", "?code=c&state=outro", {}],
    ["nonce errado", "?code=c&state=st-1", { nonce: "outro" }],
    ["e-mail não verificado", "?code=c&state=st-1", { email_verified: false }],
    ["audience errada", "?code=c&state=st-1", { aud: "outro-client" }],
  ])("%s → 403", async (_n, qs, claimOverride) => {
    mockFetch({ [GOOGLE.token]: { json: { access_token: "a", id_token: idToken({ ...goodClaims("mauro@example.test"), ...claimOverride }) } } });
    const res = await loginCallback(new Request(`${ORIGIN}/api/auth/callback${qs}`, { headers: { cookie: await loginCookie() } }));
    expect(res.status).toBe(403);
  });

  it("cookie de login expirado → 403", async () => {
    mockFetch({});
    const res = await loginCallback(
      new Request(`${ORIGIN}/api/auth/callback?code=c&state=st-1`, { headers: { cookie: await loginCookie({ exp: 1 }) } }),
    );
    expect(res.status).toBe(403);
  });
});
