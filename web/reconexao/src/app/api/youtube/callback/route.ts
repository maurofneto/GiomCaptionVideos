import { sha256Hex } from "@/lib/crypto";
import {
  exchangeCode,
  listManagedChannelIds,
  revokeToken,
  testConnection,
  YOUTUBE_SCOPE,
  type OAuthClient,
} from "@/lib/google";
import { requireAccess } from "@/lib/guard";
import { clearCookieHeader, redirectTo } from "@/lib/http";
import { log } from "@/lib/log";
import { OAUTH_COOKIE, validateOAuth } from "@/lib/oauth-state";
import { createSecretScope, describeError, type SecretScope } from "@/lib/redact";
import type { Resultado, Revogacao } from "@/lib/messages";
import type { Access } from "@/lib/guard";
import { putRefreshToken } from "@/lib/ssm";

export const dynamic = "force-dynamic";

export async function GET(req: Request): Promise<Response> {
  // 403 para qualquer um fora da allowlist — inclusive aqui, no retorno do Google.
  const access = await requireAccess(req);
  if (access instanceof Response) return access;

  // Tokens deste request ficam registrados para redação só enquanto o handler roda: o Set de
  // segredos é global do processo e sobreviveria entre requests numa instância serverless.
  const scope = createSecretScope();
  try {
    return await handle(req, access, scope);
  } finally {
    scope.release();
  }
}

async function handle(req: Request, access: Access, scope: SecretScope): Promise<Response> {
  const { env, session } = access;

  const url = new URL(req.url);
  const code = url.searchParams.get("code");
  const state = url.searchParams.get("state");
  const googleError = url.searchParams.get("error");
  // Uso único: o cookie é apagado em TODA resposta daqui em diante.
  const clear = [clearCookieHeader(OAUTH_COOKIE)];

  // Redirect sempre para URL SEM code/state/error — só códigos classificados na query.
  const done = (res: Resultado, canal?: string, teste?: string, rev?: Revogacao): Response => {
    const q = new URLSearchParams({ res });
    if (canal) q.set("canal", canal);
    if (teste) q.set("teste", teste);
    if (rev) q.set("rev", rev);
    return redirectTo(env.publicOrigin, `/?${q.toString()}`, clear);
  };

  const checked = await validateOAuth(env.sessionSecret, req.headers.get("cookie"), state, session.sid);
  if (!checked.ok) {
    log("warn", "state_rejeitado", { motivo: checked.reason });
    return done("state_invalido");
  }
  const { secretsKey, channelId, verifier } = checked.value;

  if (googleError || !code) {
    log("info", "autorizacao_nao_concedida", { canal: secretsKey, erro: googleError ?? "sem_code" });
    return done("negado", secretsKey);
  }

  const client: OAuthClient = {
    clientId: env.youtubeClientId,
    clientSecret: env.youtubeClientSecret,
    redirectUri: env.youtubeRedirectUri,
  };

  let tokens;
  try {
    tokens = await exchangeCode(client, code, verifier);
  } catch (err) {
    log("error", "troca_codigo_falhou", { canal: secretsKey, erro: describeError(err) });
    return done("troca_falhou", secretsKey);
  }
  scope.add(tokens.accessToken);
  scope.add(tokens.refreshToken);

  // Revoga só quando a conta/escopo está comprovadamente errado (preferindo o refresh token, que
  // invalida também o access token) e OAUTH_REVOKE_ON_MISMATCH=true. Trade-off: o Google trata a
  // revogação como revogação do grant da conta para este client, o que pode derrubar um token
  // bom já gravado para OUTRO canal se a mesma conta foi usada. Em falhas de infra (SSM, rede)
  // nunca revoga.
  const revokeObtained = async (): Promise<Revogacao> => {
    if (!env.revokeOnMismatch) {
      log("info", "token_nao_revogado", { canal: secretsKey, motivo: "OAUTH_REVOKE_ON_MISMATCH=false" });
      return "desativada";
    }
    const ok = await revokeToken(tokens.refreshToken ?? tokens.accessToken);
    log("info", "token_revogado", { canal: secretsKey, ok });
    return ok ? "sim" : "falhou";
  };

  if (!tokens.scope.split(" ").includes(YOUTUBE_SCOPE)) {
    return done("escopo_insuficiente", secretsKey, undefined, await revokeObtained());
  }

  // Valida a permissão ANTES de gravar: a conta tem de gerenciar o canal escolhido.
  let managed: string[];
  try {
    managed = await listManagedChannelIds(tokens.accessToken);
  } catch (err) {
    log("error", "validacao_mine_falhou", { canal: secretsKey, erro: describeError(err) });
    return done("validacao_falhou", secretsKey);
  }
  if (!managed.includes(channelId)) {
    log("warn", "canal_nao_gerenciado", { canal: secretsKey });
    return done("canal_nao_gerenciado", secretsKey, undefined, await revokeObtained());
  }

  if (!tokens.refreshToken) {
    log("warn", "sem_refresh_token", { canal: secretsKey });
    return done("sem_refresh_token", secretsKey);
  }

  try {
    await putRefreshToken(secretsKey, tokens.refreshToken, {
      channelId,
      scope: tokens.scope,
      accountHash: (await sha256Hex([...managed].sort().join(","))).slice(0, 8),
    });
  } catch (err) {
    log("error", "ssm_put_falhou", { canal: secretsKey, erro: describeError(err) });
    return done("falha_ssm", secretsKey);
  }

  const teste = await testConnection(client, tokens.refreshToken);
  log("info", "conectado", { canal: secretsKey, teste: teste.code, detalhe: teste.detail });
  return done("conectado", secretsKey, teste.code);
}
