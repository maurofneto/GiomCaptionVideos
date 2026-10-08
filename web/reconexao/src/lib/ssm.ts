import { GetParameterCommand, PutParameterCommand, SSMClient } from "@aws-sdk/client-ssm";
import { sha256Hex } from "./crypto";
import { getEnv } from "./env";
import { ssmPath } from "./secrets-key";

let client: SSMClient | undefined;

function ssm(): SSMClient {
  if (!client) {
    const env = getEnv();
    client = new SSMClient({
      region: env.awsRegion,
      credentials: { accessKeyId: env.awsAccessKeyId, secretAccessKey: env.awsSecretAccessKey },
    });
  }
  return client;
}

export function resetSsmClientForTests(): void {
  client = undefined;
}

export interface RefreshTokenMeta {
  channelId: string;
  scope: string;
  accountHash: string;
}

/** Descrição só com metadados NÃO secretos (limite do SSM: 1024). */
function describe(parts: Record<string, string | number>): string {
  return Object.entries(parts)
    .map(([k, v]) => `${k}=${v}`)
    .join("; ")
    .slice(0, 1000);
}

export async function putRefreshToken(secretsKey: string, token: string, meta: RefreshTokenMeta): Promise<void> {
  const env = getEnv();
  await ssm().send(
    new PutParameterCommand({
      Name: ssmPath(env.ssmPrefix, secretsKey, "refreshToken"),
      Value: token,
      Type: "SecureString",
      KeyId: env.kmsKeyId,
      Overwrite: true,
      Description: describe({
        ChannelId: meta.channelId,
        Conta: meta.accountHash,
        Escopo: meta.scope.replace("https://www.googleapis.com/auth/", ""),
        ModoApp: env.oauthAppMode,
        TokenSha: (await sha256Hex(token)).slice(0, 8),
        ConectadoEm: new Date().toISOString(),
      }),
    }),
  );
}

export async function putCookies(secretsKey: string, content: string, kept: number, bytes: number): Promise<void> {
  const env = getEnv();
  await ssm().send(
    new PutParameterCommand({
      Name: ssmPath(env.ssmPrefix, secretsKey, "cookies"),
      Value: content,
      Type: "SecureString",
      KeyId: env.kmsKeyId,
      Overwrite: true,
      Tier: env.cookiesTier,
      Description: describe({ Cookies: kept, Bytes: bytes, AtualizadoEm: new Date().toISOString() }),
    }),
  );
}

export interface ParamStatus {
  exists: boolean;
  lastModified?: Date;
  version?: number;
  /** Tamanho aproximado do valor CIFRADO (o conteúdo nunca é lido/descriptografado). */
  encryptedBytes?: number;
}

/** WithDecryption=false SEMPRE: o app não tem kms:Decrypt e nunca lê o segredo. */
export async function getParamStatus(secretsKey: string, kind: "refreshToken" | "cookies"): Promise<ParamStatus> {
  const env = getEnv();
  try {
    const out = await ssm().send(
      new GetParameterCommand({ Name: ssmPath(env.ssmPrefix, secretsKey, kind), WithDecryption: false }),
    );
    const p = out.Parameter;
    if (!p) return { exists: false };
    return {
      exists: true,
      lastModified: p.LastModifiedDate,
      version: p.Version,
      encryptedBytes: p.Value ? Math.floor((p.Value.length * 3) / 4) : undefined,
    };
  } catch (err) {
    if ((err as { name?: string } | null)?.name === "ParameterNotFound") return { exists: false };
    throw err;
  }
}
