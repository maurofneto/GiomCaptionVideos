// secrets_key vira segmento de path no SSM — regex restrita impede "/", ".." e afins.
const SECRETS_KEY_RE = /^[a-z0-9](?:[a-z0-9_-]{0,38}[a-z0-9])?$/;
const PREFIX_RE = /^\/[a-z0-9_-]+(?:\/[a-z0-9_-]+)*$/i;

export function isValidSecretsKey(value: unknown): value is string {
  return typeof value === "string" && SECRETS_KEY_RE.test(value);
}

export function assertSecretsKey(value: unknown): string {
  if (!isValidSecretsKey(value)) throw new Error("secrets_key inválida");
  return value;
}

export function isValidSsmPrefix(value: string): boolean {
  return PREFIX_RE.test(value);
}

export type ParamKind = "refreshToken" | "cookies";

const SUFFIX: Record<ParamKind, string> = {
  refreshToken: "Google/RefreshToken",
  cookies: "YtDlp/CookiesContent",
};

export function ssmPath(prefix: string, secretsKey: string, kind: ParamKind): string {
  if (!isValidSsmPrefix(prefix)) throw new Error("SSM_PREFIX inválido");
  return `${prefix}/canais/${assertSecretsKey(secretsKey)}/${SUFFIX[kind]}`;
}
