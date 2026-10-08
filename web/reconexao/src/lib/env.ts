import { registerSecret } from "./redact";
import { isValidSsmPrefix } from "./secrets-key";

export interface AuthEnv {
  sessionSecret: string;
  allowedEmails: string[];
}

export interface Env extends AuthEnv {
  authClientId: string;
  authClientSecret: string;
  authRedirectUri: string;
  youtubeClientId: string;
  youtubeClientSecret: string;
  youtubeRedirectUri: string;
  /** Origem pública (derivada do redirect URI do YouTube) — base de redirects e checagem de Origin. */
  publicOrigin: string;
  oauthAppMode: "testing" | "production";
  oauthTestingExpiryDays: number;
  /** Revoga o token obtido quando a conta não gerencia o canal / o escopo veio errado. */
  revokeOnMismatch: boolean;
  awsRegion: string;
  awsAccessKeyId: string;
  awsSecretAccessKey: string;
  ssmPrefix: string;
  kmsKeyId: string;
  cookiesTier: "Standard" | "Advanced";
  supabaseUrl: string;
  supabaseReconexaoJwt: string;
}

type Source = Record<string, string | undefined>;

class Collector {
  readonly problems: string[] = [];
  constructor(private readonly src: Source) {}

  required(name: string): string {
    const v = this.src[name]?.trim();
    if (!v) {
      this.problems.push(`${name} ausente`);
      return "";
    }
    return v;
  }

  optional(name: string, fallback: string): string {
    return this.src[name]?.trim() || fallback;
  }

  fail(name: string, why: string): void {
    this.problems.push(`${name} ${why}`);
  }
}

function httpsUrl(c: Collector, name: string, allowLocalhostHttp = true): string {
  const v = c.required(name);
  if (!v) return v;
  try {
    const u = new URL(v);
    const local = allowLocalhostHttp && u.protocol === "http:" && u.hostname === "localhost";
    if (u.protocol !== "https:" && !local) c.fail(name, "deve ser https (ou http://localhost)");
    if (u.search || u.hash) c.fail(name, "não pode ter query/fragmento");
  } catch {
    c.fail(name, "não é uma URL válida");
  }
  return v;
}

function loadAuth(c: Collector): AuthEnv {
  const sessionSecret = c.required("SESSION_SECRET");
  if (sessionSecret && sessionSecret.length < 32) c.fail("SESSION_SECRET", "deve ter ao menos 32 caracteres");
  const allowedEmails = c
    .required("ALLOWED_EMAILS")
    .split(",")
    .map((e) => e.trim().toLowerCase())
    .filter(Boolean);
  if (allowedEmails.some((e) => !e.includes("@"))) c.fail("ALLOWED_EMAILS", "contém item que não é e-mail");
  return { sessionSecret, allowedEmails };
}

/** Só o que o middleware (Edge) precisa — evita exigir credenciais AWS no caminho de auth. */
export function loadAuthEnv(src: Source = process.env): AuthEnv {
  const c = new Collector(src);
  const auth = loadAuth(c);
  if (c.problems.length) throw new Error(`Configuração inválida: ${c.problems.join("; ")}`);
  return auth;
}

/** Valida TODAS as variáveis e lança listando só os NOMES com problema (nunca valores). */
export function loadEnv(src: Source = process.env): Env {
  const c = new Collector(src);
  const auth = loadAuth(c);

  const authRedirectUri = httpsUrl(c, "AUTH_REDIRECT_URI");
  const youtubeRedirectUri = httpsUrl(c, "YOUTUBE_REDIRECT_URI");
  const supabaseUrl = httpsUrl(c, "SUPABASE_URL");

  const mode = c.optional("OAUTH_APP_MODE", "");
  if (mode !== "testing" && mode !== "production") c.fail("OAUTH_APP_MODE", "deve ser 'testing' ou 'production'");

  const days = Number(c.optional("OAUTH_TESTING_EXPIRY_DAYS", "7"));
  if (!Number.isInteger(days) || days < 1 || days > 365) c.fail("OAUTH_TESTING_EXPIRY_DAYS", "deve ser inteiro 1..365");

  const revoke = c.optional("OAUTH_REVOKE_ON_MISMATCH", "true").toLowerCase();
  if (revoke !== "true" && revoke !== "false") c.fail("OAUTH_REVOKE_ON_MISMATCH", "deve ser 'true' ou 'false'");

  const tier = c.optional("SSM_COOKIES_TIER", "");
  if (tier !== "Standard" && tier !== "Advanced") c.fail("SSM_COOKIES_TIER", "deve ser 'Standard' ou 'Advanced'");

  const ssmPrefix = c.required("SSM_PREFIX");
  if (ssmPrefix && !isValidSsmPrefix(ssmPrefix)) c.fail("SSM_PREFIX", "inválido (ex.: /giom/dev, sem barra final)");

  const env: Env = {
    ...auth,
    authClientId: c.required("AUTH_GOOGLE_CLIENT_ID"),
    authClientSecret: c.required("AUTH_GOOGLE_CLIENT_SECRET"),
    authRedirectUri,
    youtubeClientId: c.required("YOUTUBE_GOOGLE_CLIENT_ID"),
    youtubeClientSecret: c.required("YOUTUBE_GOOGLE_CLIENT_SECRET"),
    youtubeRedirectUri,
    publicOrigin: "",
    oauthAppMode: mode === "production" ? "production" : "testing",
    oauthTestingExpiryDays: days,
    revokeOnMismatch: revoke !== "false",
    awsRegion: c.required("RECONEXAO_AWS_REGION"),
    awsAccessKeyId: c.required("RECONEXAO_AWS_ACCESS_KEY_ID"),
    awsSecretAccessKey: c.required("RECONEXAO_AWS_SECRET_ACCESS_KEY"),
    ssmPrefix,
    kmsKeyId: c.required("SSM_KMS_KEY_ID"),
    cookiesTier: tier === "Advanced" ? "Advanced" : "Standard",
    supabaseUrl,
    supabaseReconexaoJwt: c.required("SUPABASE_RECONEXAO_JWT"),
  };

  if (c.problems.length) throw new Error(`Configuração inválida: ${c.problems.join("; ")}`);

  env.publicOrigin = new URL(env.youtubeRedirectUri).origin;
  if (new URL(env.authRedirectUri).origin !== env.publicOrigin) {
    throw new Error("Configuração inválida: AUTH_REDIRECT_URI e YOUTUBE_REDIRECT_URI devem ter a mesma origem");
  }
  return env;
}

let cached: Env | undefined;

export function getEnv(): Env {
  if (!cached) {
    cached = loadEnv();
    for (const s of [
      cached.sessionSecret,
      cached.authClientSecret,
      cached.youtubeClientSecret,
      cached.awsSecretAccessKey,
      cached.supabaseReconexaoJwt,
    ]) {
      registerSecret(s);
    }
  }
  return cached;
}

export function resetEnvCacheForTests(): void {
  cached = undefined;
}
