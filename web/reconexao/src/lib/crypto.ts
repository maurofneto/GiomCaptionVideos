// Web Crypto puro (roda no proxy e no Node 24) — sem dependências.

const enc = new TextEncoder();
const dec = new TextDecoder();

export function b64urlEncode(bytes: Uint8Array): string {
  let s = "";
  for (const b of bytes) s += String.fromCharCode(b);
  return btoa(s).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

export function b64urlDecode(text: string): Uint8Array<ArrayBuffer> {
  const pad = text.length % 4 === 0 ? "" : "=".repeat(4 - (text.length % 4));
  const bin = atob(text.replace(/-/g, "+").replace(/_/g, "/") + pad);
  const out = new Uint8Array(new ArrayBuffer(bin.length));
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

export function randomToken(bytes = 32): string {
  return b64urlEncode(crypto.getRandomValues(new Uint8Array(bytes)));
}

export async function sha256Hex(text: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", enc.encode(text));
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

export async function sha256B64url(text: string): Promise<string> {
  return b64urlEncode(new Uint8Array(await crypto.subtle.digest("SHA-256", enc.encode(text))));
}

export function timingSafeEqual(a: string, b: string): boolean {
  const x = enc.encode(a);
  const y = enc.encode(b);
  let diff = x.length ^ y.length;
  for (let i = 0; i < Math.max(x.length, y.length); i++) diff |= (x[i] ?? 0) ^ (y[i] ?? 0);
  return diff === 0;
}

// Chaves derivadas por finalidade (domain separation): a mesma SESSION_SECRET nunca é usada
// direto, e o cookie de sessão não serve como cookie de state nem vice-versa.
async function deriveKeyBytes(secret: string, label: string): Promise<ArrayBuffer> {
  return crypto.subtle.digest("SHA-256", enc.encode(`${label}\0${secret}`));
}

async function hmacKey(secret: string, label: string): Promise<CryptoKey> {
  return crypto.subtle.importKey("raw", await deriveKeyBytes(secret, label), { name: "HMAC", hash: "SHA-256" }, false, [
    "sign",
    "verify",
  ]);
}

async function aesKey(secret: string, label: string): Promise<CryptoKey> {
  return crypto.subtle.importKey("raw", await deriveKeyBytes(secret, label), "AES-GCM", false, ["encrypt", "decrypt"]);
}

/** payload JSON assinado (HMAC-SHA256): "<b64url(json)>.<b64url(mac)>". */
export async function signPayload(secret: string, label: string, payload: unknown): Promise<string> {
  const body = b64urlEncode(enc.encode(JSON.stringify(payload)));
  const mac = await crypto.subtle.sign("HMAC", await hmacKey(secret, label), enc.encode(body));
  return `${body}.${b64urlEncode(new Uint8Array(mac))}`;
}

export async function verifyPayload<T>(secret: string, label: string, token: string): Promise<T | null> {
  const [body, mac, extra] = token.split(".");
  if (!body || !mac || extra !== undefined) return null;
  try {
    const ok = await crypto.subtle.verify("HMAC", await hmacKey(secret, label), b64urlDecode(mac), enc.encode(body));
    return ok ? (JSON.parse(dec.decode(b64urlDecode(body))) as T) : null;
  } catch {
    return null;
  }
}

/** payload JSON criptografado (AES-256-GCM): "<b64url(iv)>.<b64url(cipher)>". */
export async function sealPayload(secret: string, label: string, payload: unknown): Promise<string> {
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const cipher = await crypto.subtle.encrypt(
    { name: "AES-GCM", iv },
    await aesKey(secret, label),
    enc.encode(JSON.stringify(payload)),
  );
  return `${b64urlEncode(iv)}.${b64urlEncode(new Uint8Array(cipher))}`;
}

export async function openPayload<T>(secret: string, label: string, token: string): Promise<T | null> {
  const [iv, cipher, extra] = token.split(".");
  if (!iv || !cipher || extra !== undefined) return null;
  try {
    const plain = await crypto.subtle.decrypt(
      { name: "AES-GCM", iv: b64urlDecode(iv) },
      await aesKey(secret, label),
      b64urlDecode(cipher),
    );
    return JSON.parse(dec.decode(plain)) as T;
  } catch {
    return null;
  }
}
