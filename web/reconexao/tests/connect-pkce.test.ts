import { describe, expect, it, vi } from "vitest";
import { sha256B64url } from "@/lib/crypto";
import { openPayload } from "@/lib/crypto";
import { OAUTH_COOKIE, type OAuthState } from "@/lib/oauth-state";
import { CANAL, GOOGLE, mockFetch, ORIGIN, sessionCookie } from "./helpers";
import { TEST_ENV } from "./test-env";

vi.mock("@aws-sdk/client-ssm", () => ({
  SSMClient: class {
    send = vi.fn();
  },
  PutParameterCommand: class {},
  GetParameterCommand: class {},
}));

import { POST as connectPost } from "@/app/api/youtube/connect/route";

describe("início do OAuth do YouTube", () => {
  it("monta a URL com offline + consent + S256 + escopo mínimo, sem include_granted_scopes", async () => {
    mockFetch({ [GOOGLE.supabase]: { json: [CANAL] } });
    vi.spyOn(console, "info").mockImplementation(() => undefined);
    const sess = await sessionCookie();
    const form = new FormData();
    form.set("secretsKey", CANAL.secrets_key);

    const res = await connectPost(
      new Request(`${ORIGIN}/api/youtube/connect`, { method: "POST", body: form, headers: { cookie: sess.header, origin: ORIGIN } }),
    );

    expect(res.status).toBe(303);
    const url = new URL(res.headers.get("location") as string);
    expect(url.origin + url.pathname).toBe("https://accounts.google.com/o/oauth2/v2/auth");
    const p = url.searchParams;
    expect(p.get("access_type")).toBe("offline");
    expect(p.get("prompt")).toBe("consent");
    expect(p.has("include_granted_scopes")).toBe(false);
    expect(p.get("scope")).toBe("https://www.googleapis.com/auth/youtube.force-ssl");
    expect(p.get("code_challenge_method")).toBe("S256");
    expect(p.get("redirect_uri")).toBe(TEST_ENV.YOUTUBE_REDIRECT_URI);
    expect(p.get("client_id")).toBe(TEST_ENV.YOUTUBE_GOOGLE_CLIENT_ID);

    // Cookie do state: httpOnly/secure/lax, criptografado, amarrado a canal + sessão + PKCE.
    const setCookie = res.headers.get("set-cookie") as string;
    expect(setCookie).toMatch(/HttpOnly/);
    expect(setCookie).toMatch(/Secure/);
    expect(setCookie).toMatch(/SameSite=Lax/);
    expect(setCookie).toMatch(/Max-Age=600/);
    const value = setCookie.split(";")[0]?.slice(`${OAUTH_COOKIE}=`.length) as string;
    expect(value).not.toContain(p.get("state") as string);
    const saved = await openPayload<OAuthState>(TEST_ENV.SESSION_SECRET as string, "oauth-state-v1", value);
    expect(saved?.state).toBe(p.get("state"));
    expect(saved?.secretsKey).toBe(CANAL.secrets_key);
    expect(saved?.sid).toBe(sess.sid);
    expect(p.get("code_challenge")).toBe(await sha256B64url(saved?.verifier as string));
  });

  it("secrets_key fora da lista de canais ativos não inicia o fluxo", async () => {
    mockFetch({ [GOOGLE.supabase]: { json: [CANAL] } });
    const sess = await sessionCookie();
    const form = new FormData();
    form.set("secretsKey", "../../etc");
    const res = await connectPost(
      new Request(`${ORIGIN}/api/youtube/connect`, { method: "POST", body: form, headers: { cookie: sess.header, origin: ORIGIN } }),
    );
    expect(res.headers.get("location")).toContain("res=canal_desconhecido");
    expect(res.headers.get("set-cookie")).toBeNull();
  });
});
