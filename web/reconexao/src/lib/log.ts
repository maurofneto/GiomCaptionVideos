import { redactDeep } from "./redact";

type Level = "info" | "warn" | "error";

/** Único ponto de log do app: tudo passa pela redação antes de sair. */
export function log(level: Level, event: string, data: Record<string, unknown> = {}): void {
  console[level](redactDeep({ event, ...data }));
}
