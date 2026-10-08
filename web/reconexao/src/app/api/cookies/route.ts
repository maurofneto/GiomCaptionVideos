import { listCanais } from "@/lib/canais";
import { MAX_INPUT_BYTES, parseNetscapeCookies } from "@/lib/cookies-netscape";
import { requireAccess } from "@/lib/guard";
import { redirectTo } from "@/lib/http";
import { log } from "@/lib/log";
import { describeError } from "@/lib/redact";
import { isValidSecretsKey } from "@/lib/secrets-key";
import { putCookies } from "@/lib/ssm";

export const dynamic = "force-dynamic";

export async function POST(req: Request): Promise<Response> {
  const access = await requireAccess(req, { checkOrigin: true });
  if (access instanceof Response) return access;
  const { env } = access;

  const back = (q: Record<string, string | number>): Response =>
    redirectTo(env.publicOrigin, `/?${new URLSearchParams(Object.entries(q).map(([k, v]) => [k, String(v)])).toString()}`);

  const declared = Number(req.headers.get("content-length") ?? "0");
  if (declared > MAX_INPUT_BYTES + 4096) return back({ res: "cookies_entrada_grande" });

  const form = await req.formData();
  const secretsKey = form.get("secretsKey");
  if (!isValidSecretsKey(secretsKey)) return back({ res: "canal_desconhecido" });

  let canalExiste = false;
  try {
    canalExiste = (await listCanais()).some((c) => c.secretsKey === secretsKey);
  } catch (err) {
    log("error", "cookies_lista_canais_falhou", { erro: describeError(err) });
  }
  if (!canalExiste) return back({ res: "canal_desconhecido" });

  // Arquivo tem prioridade sobre o textarea.
  const file = form.get("arquivo");
  const pasted = form.get("texto");
  let text = "";
  if (file instanceof File && file.size > 0) {
    if (file.size > MAX_INPUT_BYTES) return back({ res: "cookies_entrada_grande", canal: secretsKey });
    text = await file.text();
  } else if (typeof pasted === "string") {
    if (pasted.length > MAX_INPUT_BYTES) return back({ res: "cookies_entrada_grande", canal: secretsKey });
    text = pasted;
  }

  const parsed = parseNetscapeCookies(text, env.cookiesTier);
  if (!parsed.ok) {
    log("info", "cookies_rejeitados", { canal: secretsKey, erro: parsed.error, linha: parsed.line, bytes: parsed.bytes });
    const code = { vazio: "cookies_vazio", cabecalho: "cookies_cabecalho", linha: "cookies_linha", sem_cookies: "cookies_sem_cookies", grande: "cookies_grande" }[parsed.error];
    return back({
      res: code,
      canal: secretsKey,
      ...(parsed.line ? { linha: parsed.line } : {}),
      ...(parsed.bytes ? { bytes: parsed.bytes } : {}),
      ...(parsed.limit ? { limite: parsed.limit } : {}),
    });
  }

  try {
    await putCookies(secretsKey, parsed.content, parsed.kept, parsed.bytes);
  } catch (err) {
    log("error", "cookies_ssm_falhou", { canal: secretsKey, erro: describeError(err) });
    return back({ res: "cookies_falha_ssm", canal: secretsKey });
  }

  log("info", "cookies_atualizados", { canal: secretsKey, mantidos: parsed.kept, descartados: parsed.dropped, bytes: parsed.bytes });
  return back({ res: "cookies_ok", canal: secretsKey, mantidos: parsed.kept, bytes: parsed.bytes });
}
