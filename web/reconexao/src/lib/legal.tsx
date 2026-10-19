import type { ReactNode } from "react";

// E-mail público de contato das páginas legais exigidas pelo Google (verificação OAuth).
// Definido por env na Vercel (CONTATO_EMAIL) para não commitar e-mail pessoal.
export const CONTATO_EMAIL = process.env.CONTATO_EMAIL ?? "contato indisponível";

export function LegalPage({ titulo, children }: { titulo: string; children: ReactNode }) {
  return (
    <main style={{ maxWidth: 720, margin: "0 auto", padding: "2rem 1rem", lineHeight: 1.6 }}>
      <h1>{titulo}</h1>
      {children}
      <nav style={{ marginTop: "2rem" }}>
        <a href="/sobre">Início</a> · <a href="/privacidade">Política de Privacidade</a> ·{" "}
        <a href="/termos">Termos de Serviço</a>
      </nav>
    </main>
  );
}
