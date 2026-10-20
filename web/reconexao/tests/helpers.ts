import { vi } from "vitest";
import { loadEnv } from "@/lib/env";
import { startOAuth, OAUTH_COOKIE } from "@/lib/oauth-state";
import { createSessionToken, SESSION_COOKIE } from "@/lib/session";
import { TEST_ENV } from "./test-env";

export const ORIGIN = "https://reconexao.example.test";

// Sentinelas: nenhuma delas pode aparecer em log, Location ou corpo de resposta.
export const SENTINEL = {
  code: "TESTCODE-4/0AbCdEfGhIjKl",
  access: "ya29.TESTACCESSTOKEN-abcdefghij",
  refresh: "1//TESTREFRESHTOKEN-abcdefghijklmnopqrstu",
  idToken: "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ0ZXN0In0.TESTSIGNATURE",
  cookieValue: "SAPISID-VALUE-TEST-0001",
};

export const CANAL = { channel_id: "UCtestChannel000000000001", nome: "Canal Teste", secrets_key: "instituto" };

export async function sessionCookie(email = "mauro@example.test"): Promise<{ header: string; sid: string }> {
  const token = await createSessionToken(loadEnv(TEST_ENV), email);
  const sid = JSON.parse(Buffer.from(token.split(".")[0] as string, "base64url").toString()).sid as string;
  return { header: `${SESSION_COOKIE}=${token}`, sid };
}

export async function oauthCookie(sid: string, overrides: { now?: number } = {}) {
  const started = await startOAuth(
    TEST_ENV.SESSION_SECRET as string,
    { secretsKey: CANAL.secrets_key, channelId: CANAL.channel_id, sid },
    overrides.now,
  );
  return { state: started.state, header: `${OAUTH_COOKIE}=${started.cookieValue}`, challenge: started.challenge };
}

export interface FetchCall {
  url: string;
  method: string;
  body: string;
}

type Responder = (call: FetchCall) => { status?: number; json?: unknown } | undefined;

/** Mock de fetch por prefixo de URL; registra as chamadas. Sem rede real. */
export function mockFetch(routes: Record<string, Responder | { status?: number; json?: unknown }>) {
  const calls: FetchCall[] = [];
  const fn = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const call: FetchCall = { url, method: init?.method ?? "GET", body: String(init?.body ?? "") };
    calls.push(call);
    const key = Object.keys(routes).find((k) => url.startsWith(k));
    if (!key) throw new Error(`fetch não mockado: ${url.split("?")[0]}`);
    const r = routes[key];
    const out = typeof r === "function" ? r(call) : r;
    return new Response(JSON.stringify(out?.json ?? {}), {
      status: out?.status ?? 200,
      headers: { "Content-Type": "application/json" },
    });
  });
  vi.stubGlobal("fetch", fn);
  return { calls, fn };
}

export const GOOGLE = {
  token: "https://oauth2.googleapis.com/token",
  revoke: "https://oauth2.googleapis.com/revoke",
  channels: "https://www.googleapis.com/youtube/v3/channels",
  playlistItems: "https://www.googleapis.com/youtube/v3/playlistItems",
  captions: "https://www.googleapis.com/youtube/v3/captions",
  supabase: "https://canais.example.test/canal_ingestao",
};
