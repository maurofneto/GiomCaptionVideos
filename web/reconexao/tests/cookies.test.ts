import { beforeEach, describe, expect, it, vi } from "vitest";
import { COOKIE_LIMITS, parseNetscapeCookies } from "@/lib/cookies-netscape";
import { CANAL, GOOGLE, mockFetch, ORIGIN, sessionCookie, SENTINEL } from "./helpers";

const { send } = vi.hoisted(() => ({ send: vi.fn() }));
vi.mock("@aws-sdk/client-ssm", () => ({
  SSMClient: class {
    send = send;
  },
  PutParameterCommand: class {
    constructor(public input: Record<string, unknown>) {}
  },
  GetParameterCommand: class {},
}));

import { POST } from "@/app/api/cookies/route";

const H = "# Netscape HTTP Cookie File";
const line = (domain: string, name: string, value = "v") => [domain, "TRUE", "/", "TRUE", "1999999999", name, value].join("\t");

describe("parseNetscapeCookies", () => {
  it("mantém só youtube.com/google.com (inclusive subdomínios e #HttpOnly_) e descarta o resto", () => {
    const r = parseNetscapeCookies(
      [
        H,
        line(".youtube.com", "SID"),
        line("#HttpOnly_.google.com", "HSID"),
        line("accounts.google.com", "ACCOUNT"),
        line(".facebook.com", "fb"),
        line(".notyoutube.com", "evil"),
        line(".google.com.evil.io", "evil2"),
      ].join("\n"),
      "Standard",
    );
    expect(r.ok).toBe(true);
    if (!r.ok) return;
    expect(r.kept).toBe(3);
    expect(r.dropped).toBe(3);
    expect(r.content).not.toMatch(/facebook|notyoutube|evil/);
    expect(r.content.startsWith(`${H}\n`)).toBe(true);
  });

  it("valida cabeçalho e linhas (reporta só o número da linha)", () => {
    expect(parseNetscapeCookies("", "Standard")).toMatchObject({ ok: false, error: "vazio" });
    expect(parseNetscapeCookies(line(".youtube.com", "a"), "Standard")).toMatchObject({ ok: false, error: "cabecalho" });
    const bad = parseNetscapeCookies(`${H}\n${line(".youtube.com", "a")}\nlinha sem tabs ${SENTINEL.cookieValue}`, "Standard");
    expect(bad).toMatchObject({ ok: false, error: "linha", line: 3 });
    expect(JSON.stringify(bad)).not.toContain(SENTINEL.cookieValue);
    expect(parseNetscapeCookies(`${H}\n${line(".youtube.com", "a").replace("TRUE", "MAYBE")}`, "Standard")).toMatchObject({ error: "linha" });
    expect(parseNetscapeCookies(`${H}\n${line(".facebook.com", "a")}`, "Standard")).toMatchObject({ ok: false, error: "sem_cookies" });
  });

  it("respeita o limite do tier (4 KB Standard / 8 KB Advanced) com contagem em bytes", () => {
    const many = Array.from({ length: 60 }, (_, i) => line(".youtube.com", `C${i}`, "x".repeat(80))).join("\n");
    const std = parseNetscapeCookies(`${H}\n${many}`, "Standard");
    expect(std).toMatchObject({ ok: false, error: "grande", limit: COOKIE_LIMITS.Standard });
    expect(parseNetscapeCookies(`${H}\n${many}`, "Advanced")).toMatchObject({ ok: true });
    const huge = Array.from({ length: 120 }, (_, i) => line(".youtube.com", `C${i}`, "x".repeat(80))).join("\n");
    expect(parseNetscapeCookies(`${H}\n${huge}`, "Advanced")).toMatchObject({ ok: false, error: "grande", limit: 8192 });
  });
});

describe("POST /api/cookies", () => {
  let logs: string[];
  beforeEach(() => {
    send.mockReset();
    send.mockResolvedValue({});
    logs = [];
    for (const m of ["info", "warn", "error"] as const) {
      vi.spyOn(console, m).mockImplementation((...a: unknown[]) => void logs.push(a.map(String).join(" ")));
    }
    mockFetch({ [GOOGLE.supabase]: { json: [CANAL] } });
  });

  async function post(text: string) {
    const { header } = await sessionCookie();
    const form = new FormData();
    form.set("secretsKey", CANAL.secrets_key);
    form.set("texto", text);
    return POST(new Request(`${ORIGIN}/api/cookies`, { method: "POST", body: form, headers: { cookie: header, origin: ORIGIN } }));
  }

  it("grava só os cookies filtrados e nunca devolve/loga o conteúdo", async () => {
    const res = await post([H, line(".youtube.com", "SAPISID", SENTINEL.cookieValue), line(".facebook.com", "fb", "FBVAL")].join("\n"));

    expect(send).toHaveBeenCalledTimes(1);
    const input = (send.mock.calls[0]?.[0] as { input: Record<string, unknown> }).input;
    expect(input.Name).toBe("/giom/dev/canais/instituto/YtDlp/CookiesContent");
    expect(String(input.Value)).toContain(SENTINEL.cookieValue);
    expect(String(input.Value)).not.toContain("FBVAL");

    const loc = res.headers.get("location") as string;
    expect(new URL(loc).searchParams.get("res")).toBe("cookies_ok");
    expect([loc, await res.text(), ...logs].join("\n")).not.toContain(SENTINEL.cookieValue);
  });

  it("não grava quando excede o limite do tier", async () => {
    const big = [H, ...Array.from({ length: 80 }, (_, i) => line(".youtube.com", `C${i}`, "y".repeat(80)))].join("\n");
    const res = await post(big);
    expect(send).not.toHaveBeenCalled();
    expect(new URL(res.headers.get("location") as string).searchParams.get("res")).toBe("cookies_grande");
  });

  it("falha do SSM não vaza o conteúdo nos logs", async () => {
    send.mockRejectedValue(new Error(`falhou ${line(".youtube.com", "SAPISID", SENTINEL.cookieValue)}`));
    const res = await post([H, line(".youtube.com", "SAPISID", SENTINEL.cookieValue)].join("\n"));
    expect(new URL(res.headers.get("location") as string).searchParams.get("res")).toBe("cookies_falha_ssm");
    expect(logs.join("\n")).not.toContain(SENTINEL.cookieValue);
  });
});
