import type { Metadata } from "next";
import { CONTATO_EMAIL, LegalPage } from "@/lib/legal";

export const metadata: Metadata = { title: "Termos de Serviço — Giom Video Manager" };

export default function Termos() {
  return (
    <LegalPage titulo="Termos de Serviço">
      <p>Última atualização: 19/10/2026.</p>
      <p>
        O Giom Video Manager é uma ferramenta de uso interno do Instituto Giom e do Ministério Giom,
        restrita aos proprietários e administradores autorizados dos seus canais do YouTube. Não é
        oferecido ao público em geral.
      </p>
      <p>
        Ao autorizar o acesso, o proprietário do canal permite que a ferramenta leia e processe os
        dados do próprio canal conforme a <a href="/privacidade">Política de Privacidade</a>. O
        acesso pode ser revogado a qualquer momento em{" "}
        <a href="https://myaccount.google.com/permissions">myaccount.google.com/permissions</a>.
      </p>
      <p>O serviço é fornecido no estado em que se encontra, sem garantias. Contato: {CONTATO_EMAIL}.</p>
    </LegalPage>
  );
}
