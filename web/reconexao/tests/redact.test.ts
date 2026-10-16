import { describe, expect, it, vi } from "vitest";
import { log } from "@/lib/log";
import { describeError, redact, registerSecret } from "@/lib/redact";
import { SENTINEL } from "./helpers";

describe("redact", () => {
  const cookieLine = [".youtube.com", "TRUE", "/", "TRUE", "1999999999", "SAPISID", SENTINEL.cookieValue].join("\t");

  it("remove code/state/tokens de query strings e URLs", () => {
    const out = redact(
      `GET /api/youtube/callback?code=${SENTINEL.code}&state=abc123&scope=x refresh_token=${SENTINEL.refresh} client_secret=S3CR3T`,
    );
    for (const s of [SENTINEL.code, "abc123", SENTINEL.refresh, "S3CR3T"]) expect(out).not.toContain(s);
    expect(out).toContain("scope=x");
  });

  it("remove JSON de resposta de token, Bearer, JWT e tokens do Google soltos", () => {
    const out = redact(
      JSON.stringify({ access_token: SENTINEL.access, refresh_token: SENTINEL.refresh, id_token: SENTINEL.idToken }) +
        ` Authorization: Bearer ${SENTINEL.access} solto ${SENTINEL.refresh} jwt ${SENTINEL.idToken}`,
    );
    for (const s of [SENTINEL.access, SENTINEL.refresh, SENTINEL.idToken]) expect(out).not.toContain(s);
  });

  it("remove linhas de cookie Netscape e header Cookie", () => {
    const out = redact(`dump:\n${cookieLine}\nCookie: SID=${SENTINEL.cookieValue}`);
    expect(out).not.toContain(SENTINEL.cookieValue);
  });

  it("remove valores registrados (ClientSecret etc.), mesmo sem padrão reconhecível", () => {
    registerSecret("valor-secreto-sem-formato");
    expect(redact("falha com valor-secreto-sem-formato no meio")).not.toContain("valor-secreto-sem-formato");
  });

  it("describeError não inclui stack e redige a mensagem", () => {
    const err = new Error(`falhou https://x/cb?code=${SENTINEL.code}`);
    const out = describeError(err);
    expect(out).not.toContain(SENTINEL.code);
    expect(out).not.toContain("at ");
  });

  it("log() passa tudo pela redação (campos aninhados e Error)", () => {
    const spy = vi.spyOn(console, "error").mockImplementation(() => undefined);
    log("error", "teste", { erro: new Error(`refresh_token=${SENTINEL.refresh}`), url: `/cb?code=${SENTINEL.code}&state=zzz` });
    const printed = String(spy.mock.calls[0]?.[0]);
    for (const s of [SENTINEL.refresh, SENTINEL.code, "zzz"]) expect(printed).not.toContain(s);
    expect(printed).toContain("teste");
  });
});
