import { describe, expect, it } from "vitest";
import { loadEnv } from "@/lib/env";
import { estimateExpiry } from "@/lib/expiry";
import { TEST_ENV } from "./test-env";

describe("loadEnv", () => {
  it("aceita a configuração completa e deriva a origem pública", () => {
    const env = loadEnv(TEST_ENV);
    expect(env.publicOrigin).toBe("https://reconexao.example.test");
    expect(env.allowedEmails).toEqual(["mauro@example.test", "outro@example.test"]);
  });

  it("falha listando só os NOMES das variáveis ausentes/inválidas (nunca valores)", () => {
    const broken = { ...TEST_ENV, SESSION_SECRET: "curto", SSM_KMS_KEY_ID: "" } as Record<string, string | undefined>;
    delete broken.ALLOWED_EMAILS;
    let msg = "";
    try {
      loadEnv(broken);
    } catch (e) {
      msg = (e as Error).message;
    }
    expect(msg).toContain("SESSION_SECRET");
    expect(msg).toContain("ALLOWED_EMAILS");
    expect(msg).toContain("SSM_KMS_KEY_ID");
    expect(msg).not.toContain("curto");
  });

  it.each([
    ["OAUTH_APP_MODE", "staging"],
    ["SSM_COOKIES_TIER", "Intelligent"],
    ["SSM_PREFIX", "/giom/dev/"],
    ["YOUTUBE_REDIRECT_URI", "http://reconexao.example.test/cb"],
    ["OAUTH_TESTING_EXPIRY_DAYS", "0"],
  ])("rejeita %s=%s", (name, value) => {
    expect(() => loadEnv({ ...TEST_ENV, [name]: value })).toThrow(name);
  });

  it("OAUTH_REVOKE_ON_MISMATCH: padrão true, aceita false, rejeita lixo", () => {
    expect(loadEnv(TEST_ENV).revokeOnMismatch).toBe(true);
    expect(loadEnv({ ...TEST_ENV, OAUTH_REVOKE_ON_MISMATCH: "false" }).revokeOnMismatch).toBe(false);
    expect(() => loadEnv({ ...TEST_ENV, OAUTH_REVOKE_ON_MISMATCH: "talvez" })).toThrow("OAUTH_REVOKE_ON_MISMATCH");
  });

  it("OAUTH_TESTING_EXPIRY_DAYS tem padrão 7", () => {
    const { OAUTH_TESTING_EXPIRY_DAYS: _omit, ...rest } = TEST_ENV;
    expect(loadEnv(rest).oauthTestingExpiryDays).toBe(7);
  });
});

describe("estimateExpiry", () => {
  const connected = new Date("2026-10-01T00:00:00Z");
  const at = (h: number) => new Date(connected.getTime() + h * 3600_000);

  it("Testing: conexão + N dias, com destaque a ≤ 48 h", () => {
    expect(estimateExpiry("testing", 7, connected, at(24)).level).toBe("ok");
    const warn = estimateExpiry("testing", 7, connected, at(7 * 24 - 48));
    expect(warn.level).toBe("warn");
    expect(warn.hoursLeft).toBe(48);
    expect(estimateExpiry("testing", 7, connected, at(7 * 24 - 47)).level).toBe("warn");
    expect(estimateExpiry("testing", 7, connected, at(7 * 24 - 49)).level).toBe("ok");
    expect(estimateExpiry("testing", 7, connected, at(7 * 24 + 1)).level).toBe("expired");
  });

  it("produção ou sem data: sem estimativa", () => {
    expect(estimateExpiry("production", 7, connected).level).toBe("none");
    expect(estimateExpiry("testing", 7, undefined).level).toBe("none");
  });
});
