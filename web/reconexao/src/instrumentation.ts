// Valida TODAS as env vars no boot do servidor (falha clara listando os nomes que faltam).
export async function register(): Promise<void> {
  if (process.env.NEXT_RUNTIME === "nodejs" && process.env.NEXT_PHASE !== "phase-production-build") {
    const { getEnv } = await import("./lib/env");
    getEnv();
  }
}
