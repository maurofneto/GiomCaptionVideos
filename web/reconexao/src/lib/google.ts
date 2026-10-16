import { describeError } from "./redact";

export const YOUTUBE_SCOPE = "https://www.googleapis.com/auth/youtube.force-ssl";

const AUTH_URL = "https://accounts.google.com/o/oauth2/v2/auth";
const TOKEN_URL = "https://oauth2.googleapis.com/token";
const REVOKE_URL = "https://oauth2.googleapis.com/revoke";
const YT_API = "https://www.googleapis.com/youtube/v3";
const TIMEOUT_MS = 15_000;

export interface OAuthClient {
  clientId: string;
  clientSecret: string;
  redirectUri: string;
}

export interface TokenResponse {
  accessToken: string;
  refreshToken?: string;
  scope: string;
  idToken?: string;
}

/** Erro de chamada ao Google já reduzido a campos seguros (status + reason), sem corpo bruto. */
export class GoogleApiError extends Error {
  constructor(
    readonly status: number,
    readonly reason: string,
  ) {
    super(`Google API ${status} ${reason}`);
    this.name = "GoogleApiError";
  }
}

export function buildYoutubeAuthUrl(client: OAuthClient, state: string, challenge: string): string {
  const p = new URLSearchParams({
    client_id: client.clientId,
    redirect_uri: client.redirectUri,
    response_type: "code",
    scope: YOUTUBE_SCOPE,
    access_type: "offline",
    prompt: "consent",
    state,
    code_challenge: challenge,
    code_challenge_method: "S256",
  });
  // include_granted_scopes deliberadamente ausente.
  return `${AUTH_URL}?${p.toString()}`;
}

export function buildLoginAuthUrl(client: OAuthClient, state: string, nonce: string, challenge: string): string {
  const p = new URLSearchParams({
    client_id: client.clientId,
    redirect_uri: client.redirectUri,
    response_type: "code",
    scope: "openid email",
    state,
    nonce,
    code_challenge: challenge,
    code_challenge_method: "S256",
    prompt: "select_account",
  });
  return `${AUTH_URL}?${p.toString()}`;
}

async function readReason(res: Response): Promise<string> {
  try {
    const body = (await res.json()) as {
      error?: string | { status?: string; errors?: Array<{ reason?: string }> };
    };
    if (typeof body.error === "string") return body.error;
    return body.error?.errors?.[0]?.reason ?? body.error?.status ?? "unknown";
  } catch {
    return "unknown";
  }
}

async function call(url: string, init: RequestInit): Promise<Response> {
  try {
    return await fetch(url, { ...init, signal: AbortSignal.timeout(TIMEOUT_MS), cache: "no-store" });
  } catch (err) {
    // Mensagem do fetch pode conter a URL (com query) — só a classe do erro sobe.
    throw new GoogleApiError(0, err instanceof Error && err.name === "TimeoutError" ? "timeout" : "network");
  }
}

async function tokenRequest(params: Record<string, string>): Promise<Record<string, unknown>> {
  const res = await call(TOKEN_URL, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams(params).toString(),
  });
  if (!res.ok) throw new GoogleApiError(res.status, await readReason(res));
  return (await res.json()) as Record<string, unknown>;
}

function str(v: unknown): string | undefined {
  return typeof v === "string" && v ? v : undefined;
}

export async function exchangeCode(client: OAuthClient, code: string, verifier: string): Promise<TokenResponse> {
  const body = await tokenRequest({
    grant_type: "authorization_code",
    code,
    code_verifier: verifier,
    client_id: client.clientId,
    client_secret: client.clientSecret,
    redirect_uri: client.redirectUri,
  });
  const accessToken = str(body.access_token);
  if (!accessToken) throw new GoogleApiError(200, "no_access_token");
  return {
    accessToken,
    refreshToken: str(body.refresh_token),
    scope: str(body.scope) ?? "",
    idToken: str(body.id_token),
  };
}

export async function refreshAccessToken(client: OAuthClient, refreshToken: string): Promise<string> {
  const body = await tokenRequest({
    grant_type: "refresh_token",
    refresh_token: refreshToken,
    client_id: client.clientId,
    client_secret: client.clientSecret,
  });
  const accessToken = str(body.access_token);
  if (!accessToken) throw new GoogleApiError(200, "no_access_token");
  return accessToken;
}

/** Best effort: devolve true se o Google aceitou a revogação. */
export async function revokeToken(token: string): Promise<boolean> {
  try {
    const res = await call(REVOKE_URL, {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams({ token }).toString(),
    });
    return res.ok;
  } catch {
    return false;
  }
}

async function ytGet<T>(path: string, accessToken: string): Promise<T> {
  const res = await call(`${YT_API}${path}`, { headers: { Authorization: `Bearer ${accessToken}` } });
  if (!res.ok) throw new GoogleApiError(res.status, await readReason(res));
  return (await res.json()) as T;
}

/** channels.list?part=id&mine=true — canais que a conta autenticada gerencia. */
export async function listManagedChannelIds(accessToken: string): Promise<string[]> {
  const body = await ytGet<{ items?: Array<{ id?: string }> }>("/channels?part=id&mine=true&maxResults=50", accessToken);
  return (body.items ?? []).map((i) => i.id).filter((id): id is string => typeof id === "string");
}

export type TesteCodigo =
  | "ok"
  | "sem_video"
  | "token_invalido"
  | "sem_permissao"
  | "quota"
  | "api_desativada"
  | "rede"
  | "desconhecido";

export function classifyGoogleError(err: unknown): TesteCodigo {
  if (!(err instanceof GoogleApiError)) return "desconhecido";
  if (err.status === 0) return "rede";
  const r = err.reason;
  if (r === "invalid_grant" || r === "invalid_client" || err.status === 401) return "token_invalido";
  if (/quota|rateLimit|dailyLimit/i.test(r)) return "quota";
  if (/accessNotConfigured|SERVICE_DISABLED/i.test(r)) return "api_desativada";
  if (err.status === 403 || /forbidden|insufficientPermissions/i.test(r)) return "sem_permissao";
  return "desconhecido";
}

/**
 * Teste pós-conexão: refresh com o token recém-gravado + captions.list num vídeo recente
 * (uploads playlist). Nunca lança — devolve o resultado classificado.
 */
export async function testConnection(client: OAuthClient, refreshToken: string): Promise<{ code: TesteCodigo; detail?: string }> {
  try {
    const access = await refreshAccessToken(client, refreshToken);
    const ch = await ytGet<{ items?: Array<{ contentDetails?: { relatedPlaylists?: { uploads?: string } } }> }>(
      "/channels?part=contentDetails&mine=true&maxResults=50",
      access,
    );
    const uploads = ch.items?.[0]?.contentDetails?.relatedPlaylists?.uploads;
    if (!uploads) return { code: "sem_video" };
    const pl = await ytGet<{ items?: Array<{ contentDetails?: { videoId?: string } }> }>(
      `/playlistItems?part=contentDetails&playlistId=${encodeURIComponent(uploads)}&maxResults=1`,
      access,
    );
    const videoId = pl.items?.[0]?.contentDetails?.videoId;
    if (!videoId) return { code: "sem_video" };
    await ytGet(`/captions?part=snippet&videoId=${encodeURIComponent(videoId)}`, access);
    return { code: "ok" };
  } catch (err) {
    return { code: classifyGoogleError(err), detail: describeError(err) };
  }
}

/** Claims mínimas do id_token do login (recebido direto do token endpoint por TLS). */
export function readIdTokenClaims(idToken: string): Record<string, unknown> | null {
  const part = idToken.split(".")[1];
  if (!part) return null;
  try {
    const json = new TextDecoder().decode(
      Uint8Array.from(atob(part.replace(/-/g, "+").replace(/_/g, "/")), (c) => c.charCodeAt(0)),
    );
    return JSON.parse(json) as Record<string, unknown>;
  } catch {
    return null;
  }
}
