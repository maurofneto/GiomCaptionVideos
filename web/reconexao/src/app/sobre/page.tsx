import type { Metadata } from "next";
import { CONTATO_EMAIL, LegalPage } from "@/lib/legal";

export const metadata: Metadata = { title: "Giom Video Manager" };

export default function Sobre() {
  return (
    <LegalPage titulo="Giom Video Manager">
      <p>
        O Giom Video Manager é uma ferramenta interna do Instituto Giom e do Ministério Giom para
        processar os vídeos dos <strong>próprios canais</strong> dessas organizações no YouTube:
        listar vídeos, obter legendas, gerar transcrições e traduções e alimentar um assistente de
        perguntas e respostas sobre o conteúdo de ensino das duas organizações.
      </p>
      <p>
        O acesso aos dados do YouTube ocorre somente com a autorização do proprietário do canal, e
        apenas para os canais do Instituto Giom e do Ministério Giom. Detalhes em nossa{" "}
        <a href="/privacidade">Política de Privacidade</a>.
      </p>
      <p>Contato: {CONTATO_EMAIL}</p>
    </LegalPage>
  );
}
