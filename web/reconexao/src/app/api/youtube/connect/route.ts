import { listCanais } from "@/lib/canais";
import { buildYoutubeAuthUrl } from "@/lib/google";
import { requireAccess } from "@/lib/guard";
import { redirectTo, setCookieHeader } from "@/lib/http";
import { log } from "@/lib/log";
import { OAUTH_COOKIE, OAUTH_TTL_SECONDS, startOAuth } from "@/lib/oauth-state";
import { describeError } from "@/lib/redact";
import { isValidSecretsKey } from "@/lib/secrets-key";

export const dynamic = "force-dynamic";

export async function POST(req: Request): Promise<Response> {
  const access = await requireAccess(req, { checkOrigin: true });
  if (access instanceof Response) return access;
  const { env, session } = access;

  const form = await req.formData();
  const secretsKey = form.get("secretsKey");

  try {
    const canal = isValidSecretsKey(secretsKey) ? (await listCanais()).find((c) => c.secretsKey === secretsKey) : undefined;
    if (!canal) return redirectTo(env.publicOrigin, "/?res=canal_desconhecido");

    const started = await startOAuth(env.sessionSecret, {
      secretsKey: canal.secretsKey,
      channelId: canal.channelId,
      sid: session.sid,
    });
    const url = buildYoutubeAuthUrl(
      { clientId: env.youtubeClientId, clientSecret: env.youtubeClientSecret, redirectUri: env.youtubeRedirectUri },
      started.state,
      started.challenge,
    );
    log("info", "conexao_iniciada", { canal: canal.secretsKey });
    return new Response(null, {
      status: 303,
      headers: {
        Location: url,
        "Cache-Control": "no-store",
        "Set-Cookie": setCookieHeader(OAUTH_COOKIE, started.cookieValue, { maxAge: OAUTH_TTL_SECONDS }),
      },
    });
  } catch (err) {
    log("error", "conexao_inicio_erro", { erro: describeError(err) });
    return redirectTo(env.publicOrigin, "/?res=canal_desconhecido");
  }
}
