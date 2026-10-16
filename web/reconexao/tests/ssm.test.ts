import { beforeEach, describe, expect, it, vi } from "vitest";
import { assertSecretsKey, isValidSecretsKey, ssmPath } from "@/lib/secrets-key";

const { send } = vi.hoisted(() => ({ send: vi.fn() }));
vi.mock("@aws-sdk/client-ssm", () => ({
  SSMClient: class {
    send = send;
  },
  PutParameterCommand: class {
    constructor(public input: Record<string, unknown>) {}
    readonly kind = "put";
  },
  GetParameterCommand: class {
    constructor(public input: Record<string, unknown>) {}
    readonly kind = "get";
  },
}));

import { getParamStatus, putCookies } from "@/lib/ssm";

beforeEach(() => {
  send.mockReset();
});

describe("secrets_key e path SSM", () => {
  it("monta os dois paths esperados", () => {
    expect(ssmPath("/giom/dev", "instituto", "refreshToken")).toBe("/giom/dev/canais/instituto/Google/RefreshToken");
    expect(ssmPath("/giom/prod", "ministerio", "cookies")).toBe("/giom/prod/canais/ministerio/YtDlp/CookiesContent");
  });

  it.each(["../x", "a/b", "a..b", "", "UPPER", "-ab", "ab-", "a b", "a%2fb", "x".repeat(41), "a\nb", "a.b"])(
    "rejeita secrets_key %j",
    (bad) => {
      expect(isValidSecretsKey(bad)).toBe(false);
      expect(() => assertSecretsKey(bad)).toThrow();
      expect(() => ssmPath("/giom/dev", bad, "refreshToken")).toThrow();
    },
  );

  it.each(["instituto", "ministerio", "canal-novo_2", "a"])("aceita secrets_key %j", (ok) => {
    expect(isValidSecretsKey(ok)).toBe(true);
  });

  it("rejeita prefixo malformado", () => {
    expect(() => ssmPath("/giom/dev/", "instituto", "cookies")).toThrow();
    expect(() => ssmPath("/giom/../x", "instituto", "cookies")).toThrow();
  });
});

describe("GetParameter / PutParameter", () => {
  it("GetParameter SEMPRE com WithDecryption=false, para os dois tipos", async () => {
    send.mockResolvedValue({ Parameter: { Version: 3, LastModifiedDate: new Date("2026-10-01T00:00:00Z"), Value: "Y2lwaGVydGV4dA==" } });
    for (const kind of ["refreshToken", "cookies"] as const) {
      const st = await getParamStatus("instituto", kind);
      expect(st).toMatchObject({ exists: true, version: 3 });
    }
    expect(send).toHaveBeenCalledTimes(2);
    for (const [cmd] of send.mock.calls as Array<[{ kind: string; input: Record<string, unknown> }]>) {
      expect(cmd.kind).toBe("get");
      expect(cmd.input.WithDecryption).toBe(false);
    }
  });

  it("parâmetro inexistente vira exists=false", async () => {
    class ParameterNotFound extends Error {
      override name = "ParameterNotFound";
    }
    send.mockImplementation(() => Promise.reject(new ParameterNotFound("nf")));
    await expect(getParamStatus("instituto", "cookies")).resolves.toEqual({ exists: false });
  });

  it("putCookies usa SecureString, Overwrite, KMS e o tier da env", async () => {
    send.mockResolvedValue({});
    await putCookies("ministerio", "conteudo", 2, 8);
    const cmd = send.mock.calls[0]?.[0] as { input: Record<string, unknown> };
    expect(cmd.input).toMatchObject({
      Name: "/giom/dev/canais/ministerio/YtDlp/CookiesContent",
      Type: "SecureString",
      Overwrite: true,
      KeyId: "alias/giom-dev-test",
      Tier: "Standard",
    });
    expect(cmd.input).not.toHaveProperty("Tags");
    expect(String(cmd.input.Description)).not.toContain("conteudo");
  });
});
