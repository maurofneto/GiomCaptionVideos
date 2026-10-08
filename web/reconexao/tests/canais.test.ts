import { beforeEach, describe, expect, it, vi } from "vitest";
import { listCanais } from "@/lib/canais";
import { TEST_ENV } from "./test-env";
import { CANAL, mockFetch } from "./helpers";

function headersOf(fn: ReturnType<typeof mockFetch>["fn"], i = 0): Record<string, string> {
  return (fn.mock.calls[i]?.[1] as RequestInit).headers as Record<string, string>;
}

let logs: string[];
beforeEach(() => {
  logs = [];
  for (const m of ["info", "warn", "error"] as const) {
    vi.spyOn(console, m).mockImplementation((...a: unknown[]) => void logs.push(a.map(String).join(" ")));
  }
});

describe("listCanais — fonte de canais", () => {
  it("monta a URL sem prefixo (dev/Railway) e sempre manda Bearer; sem apikey por padrão", async () => {
    const f = mockFetch({ "https://canais.example.test/": { json: [CANAL] } });
    const canais = await listCanais();

    expect(canais).toEqual([{ channelId: CANAL.channel_id, nome: CANAL.nome, secretsKey: CANAL.secrets_key }]);
    const url = f.calls[0]?.url as string;
    expect(url.startsWith("https://canais.example.test/canal_ingestao?")).toBe(true);
    expect(url).toContain("select=channel_id,nome,secrets_key");
    expect(url).toContain("ativo=eq.true");
    const h = headersOf(f.fn);
    expect(h.Authorization).toBe(`Bearer ${TEST_ENV.CANAIS_API_TOKEN}`);
    expect(h).not.toHaveProperty("apikey");
  });

  it("monta a URL com prefixo /rest/v1 (prod/Supabase) e manda apikey só quando definido", async () => {
    vi.stubEnv("CANAIS_API_REST_PREFIX", "/rest/v1");
    vi.stubEnv("CANAIS_API_KEY", "publishable-key-test");
    const f = mockFetch({ "https://canais.example.test/": { json: [] } });
    await listCanais();

    expect((f.calls[0]?.url as string).startsWith("https://canais.example.test/rest/v1/canal_ingestao?")).toBe(true);
    const h = headersOf(f.fn);
    expect(h.apikey).toBe("publishable-key-test");
    expect(h.Authorization).toBe(`Bearer ${TEST_ENV.CANAIS_API_TOKEN}`);
    // O JWT da role nunca vai como apikey.
    expect(h.apikey).not.toBe(TEST_ENV.CANAIS_API_TOKEN);
  });

  it("nunca faz requisição sem Bearer (token ausente = falha de config antes de qualquer fetch)", async () => {
    vi.stubEnv("CANAIS_API_TOKEN", "");
    const f = mockFetch({ "https://canais.example.test/": { json: [CANAL] } });
    await expect(listCanais()).rejects.toThrow("CANAIS_API_TOKEN");
    expect(f.calls).toHaveLength(0);
  });

  it.each([401, 403])("HTTP %i da fonte vira erro e log só com o status, sem vazar token/URL", async (status) => {
    vi.stubEnv("CANAIS_API_KEY", "publishable-key-test");
    mockFetch({ "https://canais.example.test/": { status, json: { message: "denied" } } });

    await expect(listCanais()).rejects.toMatchObject({ name: "FonteCanaisError", status });

    const surface = logs.join("\n");
    expect(surface).toContain("fonte_canais_negou");
    expect(surface).toContain(String(status));
    for (const secret of [TEST_ENV.CANAIS_API_TOKEN as string, "publishable-key-test", "canal_ingestao", "select="]) {
      expect(surface).not.toContain(secret);
    }
  });

  it("descarta linhas com secrets_key inválida", async () => {
    mockFetch({ "https://canais.example.test/": { json: [CANAL, { ...CANAL, channel_id: "UCx", secrets_key: "../x" }] } });
    expect(await listCanais()).toHaveLength(1);
  });
});
