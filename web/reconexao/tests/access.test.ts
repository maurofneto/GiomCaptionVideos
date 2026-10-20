import { NextRequest } from "next/server";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { CANAL, GOOGLE, mockFetch, oauthCookie, ORIGIN, sessionCookie } from "./helpers";

const { send } = vi.hoisted(() => ({ send: vi.fn() }));
vi.mock("@aws-sdk/client-ssm", () => ({
  SSMClient: class {
    send = send;
  },
  PutParameterCommand: class {
    constructor(public input: Record<string, unknown>) {}
  },
  GetParameterCommand: class {
    constructor(public input: Record<string, unknown>) {}
  },
}));

import { POST as cookiesPost } from "@/app/api/cookies/route";
import { POST as connectPost } from "@/app/api/youtube/connect/route";
import { GET as ytCallback } from "@/app/api/youtube/callback/route";
import { proxy } from "@/proxy";

const OUTSIDER = "intruso@example.test";

beforeEach(() => {
  send.mockReset();
  vi.spyOn(console, "warn").mockImplementation(() => undefined);
  vi.spyOn(console, "info").mockImplementation(() => undefined);
});

describe("allowlist — proxy", () => {
  it("e-mail fora da allowlist recebe 403 em páginas e APIs", async () => {
    const { header } = await sessionCookie(OUTSIDER);
    for (const path of ["/", "/api/cookies", "/api/youtube/connect", "/api/youtube/callback?code=x&state=y"]) {
      const res = await proxy(new NextRequest(`${ORIGIN}${path}`, { headers: { cookie: header } }));
      expect(res.status, path).toBe(403);
    }
  });

  it("sem sessão: API recebe 403; navegação é levada ao sign-in; rotas de login ficam abertas", async () => {
    const api = await proxy(new NextRequest(`${ORIGIN}/api/cookies`, { method: "POST" }));
    expect(api.status).toBe(403);

    const nav = await proxy(new NextRequest(`${ORIGIN}/`, { headers: { accept: "text/html" } }));
    expect(nav.status).toBe(303);
    expect(nav.headers.get("location")).toContain("/api/auth/login");

    const login = await proxy(new NextRequest(`${ORIGIN}/api/auth/login`));
    expect(login.status).toBe(200);
  });

  it("e-mail da allowlist passa e a resposta leva CSP com nonce", async () => {
    const { header } = await sessionCookie();
    const res = await proxy(new NextRequest(`${ORIGIN}/`, { headers: { cookie: header } }));
    expect(res.status).toBe(200);
    const csp = res.headers.get("content-security-policy") ?? "";
    expect(csp).toMatch(/script-src 'self' 'nonce-[\w-]+' 'strict-dynamic'/);
    expect(csp).toContain("frame-ancestors 'none'");
    expect(csp).toContain("object-src 'none'");
  });

  it("sessão adulterada é tratada como sem sessão", async () => {
    const { header } = await sessionCookie();
    const res = await proxy(new NextRequest(`${ORIGIN}/api/cookies`, { headers: { cookie: `${header}x` } }));
    expect(res.status).toBe(403);
  });
});

describe("allowlist — cada handler (defesa em profundidade)", () => {
  it("callback do YouTube devolve 403 a quem está fora da allowlist e não toca Google/SSM", async () => {
    const f = mockFetch({});
    const sess = await sessionCookie(OUTSIDER);
    const oauth = await oauthCookie(sess.sid);
    const res = await ytCallback(
      new Request(`${ORIGIN}/api/youtube/callback?code=abc&state=${oauth.state}`, {
        headers: { cookie: `${sess.header}; ${oauth.header}` },
      }),
    );
    expect(res.status).toBe(403);
    expect(f.calls).toHaveLength(0);
    expect(send).not.toHaveBeenCalled();
  });

  it("callback do YouTube devolve 403 sem sessão", async () => {
    const res = await ytCallback(new Request(`${ORIGIN}/api/youtube/callback?code=abc&state=s`));
    expect(res.status).toBe(403);
  });

  it("connect e cookies devolvem 403 fora da allowlist", async () => {
    mockFetch({ [GOOGLE.supabase]: { json: [CANAL] } });
    const { header } = await sessionCookie(OUTSIDER);
    const form = new FormData();
    form.set("secretsKey", CANAL.secrets_key);
    for (const handler of [connectPost, cookiesPost]) {
      const res = await handler(new Request(`${ORIGIN}/x`, { method: "POST", body: form, headers: { cookie: header, origin: ORIGIN } }));
      expect(res.status).toBe(403);
    }
    expect(send).not.toHaveBeenCalled();
  });

  it("POST com Origin diferente é recusado mesmo com sessão válida (CSRF)", async () => {
    mockFetch({ [GOOGLE.supabase]: { json: [CANAL] } });
    const { header } = await sessionCookie();
    const form = new FormData();
    form.set("secretsKey", CANAL.secrets_key);
    const res = await connectPost(
      new Request(`${ORIGIN}/api/youtube/connect`, { method: "POST", body: form, headers: { cookie: header, origin: "https://evil.example" } }),
    );
    expect(res.status).toBe(403);
  });
});
