export const COOKIE_LIMITS = { Standard: 4096, Advanced: 8192 } as const;
export type CookieTier = keyof typeof COOKIE_LIMITS;

/** Limite de bytes aceito no upload/textarea antes de qualquer parse (anti abuso de memória). */
export const MAX_INPUT_BYTES = 512 * 1024;

const HEADER = "# Netscape HTTP Cookie File";
const HEADER_RE = /^#\s*(Netscape )?HTTP Cookie File\s*$/i;
const ALLOWED_DOMAINS = ["youtube.com", "google.com"];

export type CookieError = "vazio" | "cabecalho" | "linha" | "grande" | "sem_cookies";

export type CookieParse =
  | { ok: true; content: string; kept: number; dropped: number; bytes: number }
  | { ok: false; error: CookieError; line?: number; bytes?: number; limit?: number };

function domainAllowed(rawDomain: string): boolean {
  const d = rawDomain.replace(/^#HttpOnly_/, "").replace(/^\./, "").toLowerCase();
  return ALLOWED_DOMAINS.some((a) => d === a || d.endsWith(`.${a}`));
}

/**
 * Valida o formato Netscape e mantém só cookies de youtube.com/google.com. Mensagens de erro
 * trazem apenas número de linha/contagens — nunca o conteúdo.
 */
export function parseNetscapeCookies(text: string, tier: CookieTier): CookieParse {
  const lines = text.replace(/^﻿/, "").split(/\r?\n/);
  const first = lines.findIndex((l) => l.trim() !== "");
  if (first < 0) return { ok: false, error: "vazio" };
  if (!HEADER_RE.test((lines[first] ?? "").trim())) return { ok: false, error: "cabecalho", line: first + 1 };

  const kept: string[] = [];
  let dropped = 0;

  for (let i = first + 1; i < lines.length; i++) {
    const line = lines[i] ?? "";
    if (line.trim() === "") continue;
    if (line.startsWith("#") && !line.startsWith("#HttpOnly_")) continue; // comentário

    const f = line.split("\t");
    const valid =
      f.length === 7 &&
      (f[0] ?? "").length > 0 &&
      (f[1] === "TRUE" || f[1] === "FALSE") &&
      (f[2] ?? "").startsWith("/") &&
      (f[3] === "TRUE" || f[3] === "FALSE") &&
      /^\d+$/.test(f[4] ?? "") &&
      (f[5] ?? "").length > 0;
    if (!valid) return { ok: false, error: "linha", line: i + 1 };

    if (domainAllowed(f[0] ?? "")) kept.push(line);
    else dropped++;
  }

  if (kept.length === 0) return { ok: false, error: "sem_cookies" };

  const content = `${HEADER}\n${kept.join("\n")}\n`;
  const bytes = new TextEncoder().encode(content).length;
  const limit = COOKIE_LIMITS[tier];
  if (bytes > limit) return { ok: false, error: "grande", bytes, limit };
  return { ok: true, content, kept: kept.length, dropped, bytes };
}
