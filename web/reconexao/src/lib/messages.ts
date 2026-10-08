// O resultado da última tentativa viaja na URL só como CÓDIGOS classificados (nunca texto livre
// vindo de terceiros). A página traduz para mensagem aqui.

export type Tone = "ok" | "erro" | "aviso";

export const RESULTADOS = [
  "conectado",
  "state_invalido",
  "negado",
  "sem_refresh_token",
  "escopo_insuficiente",
  "canal_nao_gerenciado",
  "troca_falhou",
  "validacao_falhou",
  "falha_ssm",
  "canal_desconhecido",
  "cookies_ok",
  "cookies_vazio",
  "cookies_cabecalho",
  "cookies_linha",
  "cookies_sem_cookies",
  "cookies_grande",
  "cookies_entrada_grande",
  "cookies_falha_ssm",
] as const;
export type Resultado = (typeof RESULTADOS)[number];

export const TESTES = [
  "ok",
  "sem_video",
  "token_invalido",
  "sem_permissao",
  "quota",
  "api_desativada",
  "rede",
  "desconhecido",
] as const;

/** Se o token obtido foi revogado após conta/escopo errado. */
export const REVOGACOES = ["sim", "desativada", "falhou"] as const;
export type Revogacao = (typeof REVOGACOES)[number];

function sufixoRevogacao(r?: Revogacao): string {
  switch (r) {
    case "sim":
      return " O token obtido foi revogado (isso pode revogar também a autorização anterior dessa conta para este app).";
    case "desativada":
      return " O token obtido NÃO foi revogado (revogação desativada por configuração); revogue em myaccount.google.com/permissions se necessário.";
    case "falhou":
      return " Tentei revogar o token obtido, mas o Google recusou; revogue em myaccount.google.com/permissions.";
    default:
      return "";
  }
}

export interface Detalhes {
  revogacao?: Revogacao;
  linha?: number;
  bytes?: number;
  limite?: number;
  mantidos?: number;
}

export function mensagemResultado(r: Resultado, canal: string, d: Detalhes = {}): { tone: Tone; text: string } {
  switch (r) {
    case "conectado":
      return { tone: "ok", text: `Canal ${canal} conectado. O refresh token foi gravado no SSM.` };
    case "canal_nao_gerenciado":
      return {
        tone: "erro",
        text: `A conta autenticada não gerencia o canal ${canal}. Entre com a conta dona do canal ou selecione o canal/Brand Account correto na tela do Google. Nada foi gravado.${sufixoRevogacao(d.revogacao)}`,
      };
    case "sem_refresh_token":
      return {
        tone: "erro",
        text: "O Google não devolveu refresh token. Nada foi gravado. Remova o acesso do app em myaccount.google.com/permissions e tente de novo.",
      };
    case "escopo_insuficiente":
      return {
        tone: "erro",
        text: `A permissão do YouTube não foi concedida na tela do Google. Nada foi gravado.${sufixoRevogacao(d.revogacao)}`,
      };
    case "negado":
      return { tone: "aviso", text: "Autorização cancelada na tela do Google. Nada foi gravado." };
    case "state_invalido":
      return { tone: "erro", text: "Sessão de autorização inválida, expirada ou já usada. Clique em “Conectar YouTube” de novo." };
    case "troca_falhou":
      return { tone: "erro", text: "Falha ao trocar o código de autorização com o Google. Nada foi gravado." };
    case "validacao_falhou":
      return { tone: "erro", text: "Não foi possível validar a permissão no YouTube (channels.list). Nada foi gravado." };
    case "falha_ssm":
      return { tone: "erro", text: "Falha ao gravar no SSM. Verifique a policy IAM e a chave KMS." };
    case "canal_desconhecido":
      return { tone: "erro", text: "Canal não encontrado entre os canais ativos." };
    case "cookies_ok":
      return {
        tone: "ok",
        text: `Cookies de ${canal} atualizados (${d.mantidos ?? "?"} cookies, ${d.bytes ?? "?"} bytes).`,
      };
    case "cookies_vazio":
      return { tone: "erro", text: "Nenhum conteúdo de cookies enviado." };
    case "cookies_cabecalho":
      return { tone: "erro", text: "Formato inválido: a primeira linha deve ser “# Netscape HTTP Cookie File”." };
    case "cookies_linha":
      return {
        tone: "erro",
        text: `Formato inválido na linha ${d.linha ?? "?"}: esperado 7 campos separados por TAB (domínio, flag, path, secure, expiração, nome, valor).`,
      };
    case "cookies_sem_cookies":
      return { tone: "erro", text: "Nenhum cookie de .youtube.com ou .google.com no arquivo. Nada foi gravado." };
    case "cookies_grande":
      return {
        tone: "erro",
        text: `Cookies filtrados têm ${d.bytes ?? "?"} bytes e o limite do tier atual é ${d.limite ?? "?"} bytes. Exporte só youtube.com ou use o tier Advanced (8 KB).`,
      };
    case "cookies_entrada_grande":
      return { tone: "erro", text: "Arquivo grande demais (máx. 512 KB antes do filtro)." };
    case "cookies_falha_ssm":
      return { tone: "erro", text: "Falha ao gravar os cookies no SSM. Verifique a policy IAM, a chave KMS e o tier." };
  }
}

export function mensagemTeste(code: (typeof TESTES)[number]): { tone: Tone; text: string } {
  switch (code) {
    case "ok":
      return { tone: "ok", text: "Teste OK: refresh + captions.list num vídeo recente funcionaram." };
    case "sem_video":
      return { tone: "aviso", text: "Refresh OK, mas o canal não tem vídeo recente para testar captions.list." };
    case "token_invalido":
      return { tone: "erro", text: "Teste falhou: o token recém-gravado foi recusado (invalid_grant/401). Confira se ClientId/Secret da Vercel são os mesmos do pipeline." };
    case "sem_permissao":
      return { tone: "erro", text: "Teste falhou: sem permissão em captions.list (a conta precisa ser dona/gerente do canal)." };
    case "quota":
      return { tone: "aviso", text: "Teste inconclusivo: cota da YouTube Data API excedida." };
    case "api_desativada":
      return { tone: "erro", text: "Teste falhou: YouTube Data API v3 desativada no projeto Google Cloud." };
    case "rede":
      return { tone: "aviso", text: "Teste inconclusivo: erro de rede/timeout falando com o Google." };
    case "desconhecido":
      return { tone: "erro", text: "Teste falhou por motivo não classificado (ver logs do app)." };
  }
}
