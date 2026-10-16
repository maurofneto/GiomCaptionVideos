import { beforeEach, describe, expect, it, vi } from "vitest";
import { CANAL, GOOGLE, mockFetch, oauthCookie, ORIGIN, sessionCookie, SENTINEL } from "./helpers";

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

import { GET } from "@/app/api/youtube/callback/route";
import { mensagemResultado } from "@/lib/messages";
import { isSecretRegistered } from "@/lib/redact";

const tokenOk = { access_token: SENTINEL.access, refresh_token: SENTINEL.refresh, scope: "https://www.googleapis.com/auth/youtube.force-ssl" };

function googleRoutes(opts: { managed: string[]; token?: Record<string, unknown> }) {
  return mockFetch({
    [GOOGLE.token]: (c) =>
      c.body.includes("grant_type=authorization_code") ? { json: opts.token ?? tokenOk } : { json: { access_token: SENTINEL.access } },
    [GOOGLE.revoke]: { json: {} },
    [GOOGLE.channels]: (c) =>
      c.url.includes("part=id")
        ? { json: { items: opts.managed.map((id) => ({ id })) } }
        : { json: { items: [{ contentDetails: { relatedPlaylists: { uploads: "UUuploads" } } }] } },
    [GOOGLE.playlistItems]: { json: { items: [{ contentDetails: { videoId: "vid1" } }] } },
    [GOOGLE.captions]: { json: { items: [] } },
  });
}

async function callback(opts: { cookie?: string; state?: string; email?: string; withOauth?: boolean } = {}) {
  const sess = await sessionCookie(opts.email);
  const oauth = await oauthCookie(sess.sid);
  const cookies = [sess.header, opts.withOauth === false ? "" : oauth.header].filter(Boolean).join("; ");
  const url = `${ORIGIN}/api/youtube/callback?code=${encodeURIComponent(SENTINEL.code)}&state=${opts.state ?? oauth.state}`;
  return GET(new Request(url, { headers: { cookie: opts.cookie ?? cookies } }));
}

let logs: string[];
beforeEach(() => {
  send.mockReset();
  send.mockResolvedValue({});
  logs = [];
  for (const m of ["info", "warn", "error", "log"] as const) {
    vi.spyOn(console, m).mockImplementation((...a: unknown[]) => void logs.push(a.map(String).join(" ")));
  }
});

describe("callback do YouTube — validação mine=true", () => {
  it("padrão (OAUTH_REVOKE_ON_MISMATCH não definido): canal ausente não grava e NÃO revoga", async () => {
    const f = googleRoutes({ managed: ["UCoutroCanal"] });
    const res = await callback();

    expect(send).not.toHaveBeenCalled();
    expect(f.calls.some((c) => c.url.startsWith(GOOGLE.revoke))).toBe(false);
    const loc = new URL(res.headers.get("location") as string);
    expect(loc.searchParams.get("res")).toBe("canal_nao_gerenciado");
    expect(loc.searchParams.get("rev")).toBe("desativada");
  });

  it("OAUTH_REVOKE_ON_MISMATCH=true explícito: canal ausente não grava, revoga o token e avisa", async () => {
    vi.stubEnv("OAUTH_REVOKE_ON_MISMATCH", "true");
    const f = googleRoutes({ managed: ["UCoutroCanal"] });
    const res = await callback();

    expect(send).not.toHaveBeenCalled();
    const revoke = f.calls.find((c) => c.url.startsWith(GOOGLE.revoke));
    expect(revoke).toBeDefined();
    expect(revoke?.body).toContain("token=");
    const loc = new URL(res.headers.get("location") as string);
    expect(loc.searchParams.get("res")).toBe("canal_nao_gerenciado");
    expect(loc.searchParams.get("rev")).toBe("sim");
    expect(loc.searchParams.has("code")).toBe(false);
    expect(loc.searchParams.has("state")).toBe(false);
  });

  it("OAUTH_REVOKE_ON_MISMATCH=false: canal ausente não grava e NÃO chama revoke", async () => {
    vi.stubEnv("OAUTH_REVOKE_ON_MISMATCH", "false");
    const f = googleRoutes({ managed: ["UCoutroCanal"] });
    const res = await callback();

    expect(send).not.toHaveBeenCalled();
    expect(f.calls.some((c) => c.url.startsWith(GOOGLE.revoke))).toBe(false);
    const loc = new URL(res.headers.get("location") as string);
    expect(loc.searchParams.get("res")).toBe("canal_nao_gerenciado");
    expect(loc.searchParams.get("rev")).toBe("desativada");
  });

  it("OAUTH_REVOKE_ON_MISMATCH=false: escopo insuficiente não grava e não revoga", async () => {
    vi.stubEnv("OAUTH_REVOKE_ON_MISMATCH", "false");
    const f = googleRoutes({ managed: [CANAL.channel_id], token: { ...tokenOk, scope: "openid" } });
    const res = await callback();
    expect(send).not.toHaveBeenCalled();
    expect(f.calls.some((c) => c.url.startsWith(GOOGLE.revoke))).toBe(false);
    expect(new URL(res.headers.get("location") as string).searchParams.get("rev")).toBe("desativada");
  });

  it("a mensagem diz se houve revogação", () => {
    expect(mensagemResultado("canal_nao_gerenciado", "X", { revogacao: "sim" }).text).toMatch(/foi revogado/);
    const off = mensagemResultado("canal_nao_gerenciado", "X", { revogacao: "desativada" }).text;
    expect(off).toMatch(/Nada foi gravado\./);
    expect(off).toMatch(/Nada foi revogado\. Entre com a conta dona do canal/);
    expect(mensagemResultado("canal_nao_gerenciado", "X", { revogacao: "falhou" }).text).toMatch(/recusou/);
    expect(mensagemResultado("canal_nao_gerenciado", "Canal X").text).toContain(
      "A conta autenticada não gerencia o canal Canal X. Entre com a conta dona do canal ou selecione o canal/Brand Account correto na tela do Google.",
    );
  });

  it("canal presente: grava no path certo (SecureString, Overwrite, KMS, sem tags) e roda o teste", async () => {
    const f = googleRoutes({ managed: [CANAL.channel_id] });
    const res = await callback();

    expect(send).toHaveBeenCalledTimes(1);
    const cmd = send.mock.calls[0]?.[0] as { kind: string; input: Record<string, unknown> };
    expect(cmd.kind).toBe("put");
    expect(cmd.input).toMatchObject({
      Name: "/giom/dev/canais/instituto/Google/RefreshToken",
      Type: "SecureString",
      Overwrite: true,
      KeyId: "alias/giom-dev-test",
      Value: SENTINEL.refresh,
    });
    expect(cmd.input).not.toHaveProperty("Tags");
    const description = String(cmd.input.Description);
    expect(description).toContain(`ChannelId=${CANAL.channel_id}`);
    expect(description).toContain("ClientId=yt-client-id.test");
    expect(description).not.toContain(SENTINEL.refresh);
    expect(description).not.toContain("YT-CLIENT-SECRET-TEST");
    const conectado = logs.find((l) => l.includes('"event":"conectado"')) ?? "";
    expect(conectado).toContain('"clientId":"yt-client-id.test"');
    expect(f.calls.some((c) => c.url.startsWith(GOOGLE.captions))).toBe(true);
    expect(f.calls.some((c) => c.url.startsWith(GOOGLE.revoke))).toBe(false);

    const loc = new URL(res.headers.get("location") as string);
    expect(loc.searchParams.get("res")).toBe("conectado");
    expect(loc.searchParams.get("teste")).toBe("ok");
  });

  it("sem refresh_token: erro claro e nada gravado", async () => {
    googleRoutes({ managed: [CANAL.channel_id], token: { access_token: SENTINEL.access, scope: tokenOk.scope } });
    const res = await callback();
    expect(send).not.toHaveBeenCalled();
    expect(new URL(res.headers.get("location") as string).searchParams.get("res")).toBe("sem_refresh_token");
  });

  it("OAUTH_REVOKE_ON_MISMATCH=true: escopo sem youtube.force-ssl não grava e revoga o token", async () => {
    vi.stubEnv("OAUTH_REVOKE_ON_MISMATCH", "true");
    const f = googleRoutes({ managed: [CANAL.channel_id], token: { ...tokenOk, scope: "openid" } });
    const res = await callback();
    expect(send).not.toHaveBeenCalled();
    expect(f.calls.some((c) => c.url.startsWith(GOOGLE.revoke))).toBe(true);
    expect(new URL(res.headers.get("location") as string).searchParams.get("res")).toBe("escopo_insuficiente");
  });

  it("erro de teste é classificado (captions 403) sem desfazer a gravação", async () => {
    mockFetch({
      [GOOGLE.token]: (c) => (c.body.includes("authorization_code") ? { json: tokenOk } : { json: { access_token: SENTINEL.access } }),
      [GOOGLE.channels]: (c) =>
        c.url.includes("part=id")
          ? { json: { items: [{ id: CANAL.channel_id }] } }
          : { json: { items: [{ contentDetails: { relatedPlaylists: { uploads: "UU1" } } }] } },
      [GOOGLE.playlistItems]: { json: { items: [{ contentDetails: { videoId: "v" } }] } },
      [GOOGLE.captions]: { status: 403, json: { error: { errors: [{ reason: "forbidden" }] } } },
    });
    const res = await callback();
    expect(send).toHaveBeenCalledTimes(1);
    expect(new URL(res.headers.get("location") as string).searchParams.get("teste")).toBe("sem_permissao");
  });
});

describe("callback do YouTube — state anti-CSRF / uso único", () => {
  it("state errado → rejeita sem chamar o Google nem o SSM", async () => {
    const f = googleRoutes({ managed: [CANAL.channel_id] });
    const res = await callback({ state: "state-forjado" });
    expect(new URL(res.headers.get("location") as string).searchParams.get("res")).toBe("state_invalido");
    expect(f.calls).toHaveLength(0);
    expect(send).not.toHaveBeenCalled();
  });

  it("sem cookie de state (reuso após consumo) → rejeita", async () => {
    const f = googleRoutes({ managed: [CANAL.channel_id] });
    const ok = await callback();
    expect(ok.headers.getSetCookie().join(" ")).toMatch(/__Host-rc_oauth=;.*Max-Age=0/);

    const reuse = await callback({ withOauth: false });
    expect(new URL(reuse.headers.get("location") as string).searchParams.get("res")).toBe("state_invalido");
    expect(send).toHaveBeenCalledTimes(1);
    expect(f.calls.filter((c) => c.body.includes("authorization_code"))).toHaveLength(1);
  });

  it("state expirado → rejeita", async () => {
    googleRoutes({ managed: [CANAL.channel_id] });
    const sess = await sessionCookie();
    const old = await oauthCookie(sess.sid, { now: Date.now() - 11 * 60 * 1000 });
    const res = await GET(
      new Request(`${ORIGIN}/api/youtube/callback?code=${SENTINEL.code}&state=${old.state}`, {
        headers: { cookie: `${sess.header}; ${old.header}` },
      }),
    );
    expect(new URL(res.headers.get("location") as string).searchParams.get("res")).toBe("state_invalido");
    expect(send).not.toHaveBeenCalled();
  });

  it("state de OUTRA sessão → rejeita (amarrado à sessão)", async () => {
    googleRoutes({ managed: [CANAL.channel_id] });
    const sess = await sessionCookie();
    const other = await oauthCookie("sid-de-outra-sessao");
    const res = await GET(
      new Request(`${ORIGIN}/api/youtube/callback?code=x&state=${other.state}`, {
        headers: { cookie: `${sess.header}; ${other.header}` },
      }),
    );
    expect(new URL(res.headers.get("location") as string).searchParams.get("res")).toBe("state_invalido");
  });
});

describe("callback do YouTube — segredos por request", () => {
  it("tokens ficam registrados para redação durante o handler e são liberados ao fim", async () => {
    googleRoutes({ managed: [CANAL.channel_id] });
    const seen: boolean[] = [];
    send.mockImplementation(async () => {
      seen.push(isSecretRegistered(SENTINEL.refresh), isSecretRegistered(SENTINEL.access));
      return {};
    });
    await callback();
    expect(seen).toEqual([true, true]);
    expect(isSecretRegistered(SENTINEL.refresh)).toBe(false);
    expect(isSecretRegistered(SENTINEL.access)).toBe(false);
  });

  it("libera também quando o handler falha (finally)", async () => {
    googleRoutes({ managed: [CANAL.channel_id] });
    send.mockImplementation(async () => {
      throw new Error("falha");
    });
    await callback();
    expect(isSecretRegistered(SENTINEL.refresh)).toBe(false);

    // caminho de erro de rede no meio do fluxo
    mockFetch({
      [GOOGLE.token]: { json: tokenOk },
      [GOOGLE.channels]: () => {
        throw new Error("rede");
      },
    });
    await callback().catch(() => undefined);
    expect(isSecretRegistered(SENTINEL.access)).toBe(false);
  });

  it("segredos estáticos da env continuam registrados entre requests", async () => {
    googleRoutes({ managed: [CANAL.channel_id] });
    await callback();
    expect(isSecretRegistered("YT-CLIENT-SECRET-TEST")).toBe(true);
  });
});

describe("callback do YouTube — nunca vaza segredo", () => {
  it("nenhum log, Location ou corpo contém code/token (sucesso e falha)", async () => {
    for (const managed of [[CANAL.channel_id], ["UCoutro"]]) {
      googleRoutes({ managed });
      const res = await callback();
      const surface = [res.headers.get("location") ?? "", await res.text(), ...logs].join("\n");
      for (const secret of [SENTINEL.code, SENTINEL.access, SENTINEL.refresh, "YT-CLIENT-SECRET-TEST"]) {
        expect(surface).not.toContain(secret);
      }
    }
  });

  it("falha do SSM com mensagem contendo o token não vaza nos logs", async () => {
    googleRoutes({ managed: [CANAL.channel_id] });
    send.mockRejectedValue(new Error(`boom refresh_token=${SENTINEL.refresh} Bearer ${SENTINEL.access}`));
    const res = await callback();
    expect(new URL(res.headers.get("location") as string).searchParams.get("res")).toBe("falha_ssm");
    const surface = logs.join("\n");
    expect(surface).not.toContain(SENTINEL.refresh);
    expect(surface).not.toContain(SENTINEL.access);
  });
});
