import { clearCookieHeader, redirectTo } from "@/lib/http";
import { requireAccess } from "@/lib/guard";
import { SESSION_COOKIE } from "@/lib/session";

export const dynamic = "force-dynamic";

export async function POST(req: Request): Promise<Response> {
  const access = await requireAccess(req, { checkOrigin: true });
  if (access instanceof Response) return access;
  return redirectTo(access.env.publicOrigin, "/api/auth/login", [clearCookieHeader(SESSION_COOKIE)]);
}
