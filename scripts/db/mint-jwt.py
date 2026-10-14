#!/usr/bin/env python3
"""Gera um JWT HS256 para o PostgREST (claim `role`) — US #149. Só biblioteca padrão.

O segredo vem SÓ de variável de ambiente (nunca de argumento: não aparece no histórico/ps) e o JWT
sai só no stdout, pra ser redirecionado direto pro destino (SSM, arquivo 0600...) sem passar pela tela.

    PGRST_JWT_SECRET=... python scripts/db/mint-jwt.py --role pipeline_worker --days 365 > /dev/null
"""
import argparse, base64, hashlib, hmac, json, os, sys, time


def b64(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode()


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--role", required=True, help="role do banco assumida pelo PostgREST (ex.: pipeline_worker)")
    p.add_argument("--days", type=int, default=365, help="validade em dias (default 365; rotacionar antes)")
    p.add_argument("--secret-env", default="PGRST_JWT_SECRET", help="variável de ambiente com o JWT secret")
    a = p.parse_args()

    secret = os.environ.get(a.secret_env, "")
    if len(secret) < 32:
        print(f"erro: {a.secret_env} ausente ou menor que 32 caracteres (o PostgREST exige >= 32)", file=sys.stderr)
        return 2

    agora = int(time.time())
    header = b64(json.dumps({"alg": "HS256", "typ": "JWT"}, separators=(",", ":")).encode())
    payload = b64(json.dumps({"role": a.role, "iss": "giom", "iat": agora, "exp": agora + a.days * 86400},
                             separators=(",", ":")).encode())
    assinatura = b64(hmac.new(secret.encode(), f"{header}.{payload}".encode(), hashlib.sha256).digest())
    sys.stdout.write(f"{header}.{payload}.{assinatura}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
