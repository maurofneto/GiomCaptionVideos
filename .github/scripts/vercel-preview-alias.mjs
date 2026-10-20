// Espera o deployment da Vercel ficar READY, confirma que é Preview e só então aplica o alias fixo
// (o redirect URI do Google exige URL exata). Usa a REST API com fetch porque `vercel inspect`/
// `alias` resolvem o contexto via GET /v2/user e falham (404) com token de time/projeto.
// Entrada por env: VERCEL_TOKEN, VERCEL_ORG_ID, VERCEL_PROJECT_ID, PREVIEW_ALIAS, DEPLOY_URL.
const { VERCEL_TOKEN, VERCEL_ORG_ID, VERCEL_PROJECT_ID, PREVIEW_ALIAS, DEPLOY_URL } = process.env;
const API = "https://api.vercel.com";
const POLL_MS = 3000;
const TIMEOUT_MS = 3 * 60 * 1000;
const EM_ANDAMENTO = ["QUEUED", "INITIALIZING", "BUILDING"];

const falha = (msg) => { console.error(msg); process.exit(1); };
const vazio = (v) => !v || /^\$\(/.test(v);
if (vazio(VERCEL_TOKEN) || vazio(VERCEL_ORG_ID) || vazio(PREVIEW_ALIAS) || vazio(DEPLOY_URL)) {
  falha("Faltam VERCEL_TOKEN, VERCEL_ORG_ID, RECONEXAO_PREVIEW_ALIAS ou a URL do deployment");
}
const host = DEPLOY_URL.trim().replace(/^https?:\/\//, "").replace(/\/+$/, "");

// Só status + error.code/message da Vercel em caso de erro: nunca headers nem o objeto inteiro.
async function chamar(metodo, caminho, corpo) {
  const url = `${API}${caminho}?teamId=${encodeURIComponent(VERCEL_ORG_ID)}`;
  const headers = { Authorization: `Bearer ${VERCEL_TOKEN}` };
  if (corpo !== undefined) headers["Content-Type"] = "application/json";
  let res;
  try {
    res = await fetch(url, { method: metodo, headers, body: corpo === undefined ? undefined : JSON.stringify(corpo) });
  } catch (e) {
    falha(`${metodo} ${caminho}: falha de rede (${e?.message ?? e})`);
  }
  let json = null;
  try { json = await res.json(); } catch { /* corpo vazio ou não-JSON */ }
  if (!res.ok) {
    falha(`${metodo} ${caminho}: HTTP ${res.status} ${json?.error?.code ?? ""} ${json?.error?.message ?? ""}`.trim());
  }
  return json;
}

// Equivale ao `vercel inspect --wait`: poll até sair de QUEUED/INITIALIZING/BUILDING.
const limite = Date.now() + TIMEOUT_MS;
let d = await chamar("GET", `/v13/deployments/${encodeURIComponent(host)}`);
while (EM_ANDAMENTO.includes(d.readyState)) {
  if (Date.now() > limite) falha(`Timeout de 3 min esperando o deployment (readyState=${d.readyState})`);
  await new Promise((r) => setTimeout(r, POLL_MS));
  d = await chamar("GET", `/v13/deployments/${encodeURIComponent(host)}`);
}
if (d.readyState !== "READY") falha(`Deployment nao ficou READY (readyState=${d.readyState})`);

// Guarda: só aplica o alias se o deployment é de Preview. Na API da Vercel o target de Preview
// é null/"preview"; "production" (ou ambiente custom) falha o job.
if (!Object.hasOwn(d, "target")) falha("Resposta da API sem campo target; abortando sem alias");
if (d.target !== null && d.target !== "preview") {
  falha(`Deployment NAO eh Preview (target=${d.target}); abortando sem alias`);
}
if (d.projectId && !vazio(VERCEL_PROJECT_ID) && d.projectId !== VERCEL_PROJECT_ID) {
  falha("Deployment pertence a outro projeto (projectId != VERCEL_PROJECT_ID); abortando sem alias");
}
console.log(`Confirmado: deployment de Preview (id=${d.id}, url=${d.url}, target=${d.target}, readyState=${d.readyState})`);

const r = await chamar("POST", `/v2/deployments/${encodeURIComponent(d.id)}/aliases`, { alias: PREVIEW_ALIAS });
console.log(`Alias aplicado: ${r?.alias ?? PREVIEW_ALIAS}`);
