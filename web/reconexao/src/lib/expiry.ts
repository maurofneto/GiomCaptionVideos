export const WARN_HOURS = 48;

export type ExpiryLevel = "none" | "ok" | "warn" | "expired";

export interface Expiry {
  level: ExpiryLevel;
  expiresAt?: Date;
  hoursLeft?: number;
}

/**
 * App OAuth em Testing → refresh token vale ~N dias (padrão 7) desde a conexão. Em produção o
 * token não tem esse teto, então não há estimativa.
 */
export function estimateExpiry(
  mode: "testing" | "production",
  days: number,
  connectedAt: Date | undefined,
  now: Date = new Date(),
): Expiry {
  if (mode !== "testing" || !connectedAt) return { level: "none" };
  const expiresAt = new Date(connectedAt.getTime() + days * 24 * 3600 * 1000);
  const hoursLeft = Math.floor((expiresAt.getTime() - now.getTime()) / 3600_000);
  const level: ExpiryLevel = hoursLeft < 0 ? "expired" : hoursLeft <= WARN_HOURS ? "warn" : "ok";
  return { level, expiresAt, hoursLeft };
}

export function ageDays(from: Date | undefined, now: Date = new Date()): number | undefined {
  return from ? Math.floor((now.getTime() - from.getTime()) / (24 * 3600 * 1000)) : undefined;
}
