// Redação aplicada a TODO log/erro. Duas camadas: padrões estruturais (query string, JSON,
// Bearer, formatos de token do Google, linhas de cookie Netscape) e valores exatos de segredos
// registrados em runtime (ClientSecret, segredo de sessão, conteúdo de cookies em processamento).

const registered = new Set<string>();

export function registerSecret(value: string | undefined): void {
  if (value && value.length >= 6) registered.add(value);
}

export function clearRegisteredSecrets(): void {
  registered.clear();
}

export function isSecretRegistered(value: string): boolean {
  return registered.has(value);
}

export interface SecretScope {
  add(value: string | undefined): void;
  release(): void;
}

/**
 * Segredos de vida curta (access/refresh token de UM request). O Set é global do processo e
 * sobrevive entre requests numa instância serverless, então quem registra aqui TEM de chamar
 * release() num finally. Segredos estáticos da env continuam em registerSecret().
 */
export function createSecretScope(): SecretScope {
  const mine: string[] = [];
  return {
    add(value) {
      if (value && value.length >= 6 && !registered.has(value)) {
        registered.add(value);
        mine.push(value);
      }
    },
    release() {
      for (const v of mine.splice(0)) registered.delete(v);
    },
  };
}

const MARK = "[REDACTED]";
const SENSITIVE_KEYS = "code|state|access_token|refresh_token|id_token|client_secret|code_verifier|token|assertion|nonce";

const PATTERNS: Array<[RegExp, string]> = [
  [new RegExp(`(^|[?&\\s"'])(${SENSITIVE_KEYS})=[^&\\s"']*`, "gim"), `$1$2=${MARK}`],
  [new RegExp(`("(?:${SENSITIVE_KEYS})"\\s*:\\s*")[^"]*(")`, "gi"), `$1${MARK}$2`],
  [/Bearer\s+[A-Za-z0-9\-._~+/]+=*/g, `Bearer ${MARK}`],
  [/ya29\.[\w\-.]+/g, MARK],
  [/1\/\/[\w-]{20,}/g, MARK],
  [/eyJ[\w-]+\.[\w-]+\.[\w-]+/g, MARK],
  [/(\bcookie:\s*)[^\n]+/gi, `$1${MARK}`],
  [
    // Sem âncora de início: a linha pode vir embutida no meio de uma mensagem de erro.
    /(?:#HttpOnly_)?\.?[\w.-]+\t(?:TRUE|FALSE)\t[^\t\n]*\t(?:TRUE|FALSE)\t\d+\t[^\t\n]*\t[^\n]*/g,
    "[COOKIE-LINE]",
  ],
];

export function redact(input: string): string {
  let out = input;
  for (const [re, repl] of PATTERNS) out = out.replace(re, repl);
  for (const secret of registered) out = out.split(secret).join(MARK);
  return out;
}

/** Erro → "Nome: mensagem" redigido. Sem stack (pode carregar URL com query). */
export function describeError(err: unknown): string {
  if (err instanceof Error) return redact(`${err.name}: ${err.message}`);
  return redact(typeof err === "string" ? err : "erro desconhecido");
}

export function redactDeep(value: unknown): string {
  const json = JSON.stringify(value, (_k, v: unknown) => (v instanceof Error ? describeError(v) : v));
  return redact(json ?? "");
}
