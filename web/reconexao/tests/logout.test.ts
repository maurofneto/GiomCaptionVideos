import { NextRequest } from "next/server";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { DELETE, GET, HEAD, OPTIONS, PATCH, POST, PUT } from "@/app/api/auth/logout/route";
import { proxy } from "@/proxy";
import { ORIGIN, sessionCookie } from "./helpers";

const URL_LOGOUT = `${ORIGIN}/api/auth/logout`;

beforeEach(() => {
  vi.spyOn(console, "warn").mockImplementation(() => undefined);
  vi.spyOn(console, "info").mockImplementation(() => undefined);
});

function post(headers: Record<string, string>): Promise<Response> {
  return POST(new Request(URL_LOGOUT, { method: "POST", headers }));
}

function expectCleared(res: Response): void {
  expect(res.status).toBe(303);
  expect(res.headers.get("location")).toMatch(/\/api\/auth\/login$/);
  const cookies = res.headers.getSetCookie();
  expect(cookies).toHaveLength(2);
  for (const name of ["__Host-rc_session", "__Host-rc_login"]) {
    const c = cookies.find((x) => x.startsWith(`${name}=`));
    expect(c, name).toBeDefined();
    expect(c).toContain("Max-Age=0");
    expect(c).toContain("Path=/");
    expect(c).toContain("Secure");
    expect(c).toContain("HttpOnly");
    expect(c).toContain("SameSite=Lax");
    expect(c).not.toContain("Domain");
  }
}

describe("POST /api/auth/logout", () => {
  it("sem cookie + Origin certa → 303 ao login e limpa os dois cookies", async () => {
    expectCleared(await post({ origin: ORIGIN }));
  });

  it("sessão válida + Origin certa → limpa", async () => {
    const { header } = await sessionCookie();
    expectCleared(await post({ origin: ORIGIN, cookie: header }));
  });

  it("cookie adulterado + Origin certa → limpa", async () => {
    const { header } = await sessionCookie();
    expectCleared(await post({ origin: ORIGIN, cookie: `${header}x` }));
  });

  it("sessão fora da allowlist + Origin certa → limpa (não 403)", async () => {
    const { header } = await sessionCookie("intruso@example.test");
    expectCleared(await post({ origin: ORIGIN, cookie: header }));
  });

  it("Origin errada, mesmo com sessão válida → 403 sem Set-Cookie", async () => {
    const { header } = await sessionCookie();
    const res = await post({ origin: "https://evil.example.test", cookie: header });
    expect(res.status).toBe(403);
    expect(res.headers.getSetCookie()).toEqual([]);
  });

  it("sem Origin (Referer não vale) → 403 sem Set-Cookie", async () => {
    const { header } = await sessionCookie();
    const res = await post({ referer: `${ORIGIN}/`, cookie: header });
    expect(res.status).toBe(403);
    expect(res.headers.getSetCookie()).toEqual([]);
  });
});

describe("logout — métodos que não são POST", () => {
  it.each([
    ["GET", GET],
    ["HEAD", HEAD],
    ["PUT", PUT],
    ["PATCH", PATCH],
    ["DELETE", DELETE],
    ["OPTIONS", OPTIONS],
  ])("%s → 405, Allow: POST, sem Set-Cookie", (_method, handler) => {
    const res = handler();
    expect(res.status).toBe(405);
    expect(res.headers.get("allow")).toContain("POST");
    expect(res.headers.getSetCookie()).toEqual([]);
  });
});

describe("logout — proxy", () => {
  it("sem cookie /api/auth/logout passa; subcaminho continua protegido", async () => {
    const ok = await proxy(new NextRequest(URL_LOGOUT, { method: "POST" }));
    expect(ok.status).toBe(200);
    const blocked = await proxy(new NextRequest(`${URL_LOGOUT}/x`, { method: "POST" }));
    expect(blocked.status).toBe(403);
  });

  it("403 de e-mail fora da allowlist: GET de documento ganha form Sair; API segue texto puro", async () => {
    const { header } = await sessionCookie("intruso@example.test");
    const doc = await proxy(new NextRequest(`${ORIGIN}/`, { headers: { cookie: header, accept: "text/html" } }));
    expect(doc.status).toBe(403);
    expect(doc.headers.get("content-type")).toContain("text/html");
    const body = await doc.text();
    expect(body).toContain('action="/api/auth/logout"');
    expect(body).not.toContain("intruso");
    const api = await proxy(new NextRequest(`${ORIGIN}/api/cookies`, { method: "POST", headers: { cookie: header, accept: "text/html" } }));
    expect(api.status).toBe(403);
    expect(await api.text()).toBe("Acesso negado");
  });
});
