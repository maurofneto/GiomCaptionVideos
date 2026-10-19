import type { Metadata } from "next";
import { CONTATO_EMAIL, LegalPage } from "@/lib/legal";

export const metadata: Metadata = { title: "Política de Privacidade — Giom Video Manager" };

export default function Privacidade() {
  return (
    <LegalPage titulo="Política de Privacidade">
      <p>Última atualização: 19/10/2026.</p>
      <h2>Quem somos</h2>
      <p>
        O Giom Video Manager é operado pelo Instituto Giom e pelo Ministério Giom para processar os
        vídeos dos seus próprios canais do YouTube. Contato: {CONTATO_EMAIL}.
      </p>
      <h2>Dados acessados</h2>
      <p>
        Com a autorização do proprietário do canal, acessamos pela API do YouTube a lista de vídeos,
        os metadados e as legendas dos canais do Instituto Giom e do Ministério Giom. Não acessamos
        dados pessoais de espectadores nem de terceiros.
      </p>
      <h2>Como usamos</h2>
      <p>
        Os dados são usados exclusivamente para gerar transcrições e traduções e para alimentar um
        assistente de perguntas e respostas sobre o conteúdo das próprias organizações.
      </p>
      <h2>Compartilhamento</h2>
      <p>
        Não vendemos nem compartilhamos dados de usuários do Google com terceiros, exceto
        provedores de infraestrutura e de processamento de texto estritamente necessários à
        operação (hospedagem, banco de dados e IA), sujeitos a confidencialidade. O uso das
        informações recebidas das APIs do Google segue a{" "}
        <a href="https://developers.google.com/terms/api-services-user-data-policy">
          Política de Dados do Usuário dos Serviços de API do Google
        </a>
        , incluindo os requisitos de Uso Limitado.
      </p>
      <h2>Armazenamento e segurança</h2>
      <p>
        Tokens de acesso ficam em gerenciador de segredos criptografado (AWS SSM Parameter Store) e
        nunca em repositório de código.
      </p>
      <h2>Retenção e revogação</h2>
      <p>
        Mantemos os dados enquanto o acesso estiver autorizado. É possível revogar o acesso a
        qualquer momento em{" "}
        <a href="https://myaccount.google.com/permissions">myaccount.google.com/permissions</a> e
        solicitar a exclusão dos dados pelo e-mail de contato.
      </p>
    </LegalPage>
  );
}
