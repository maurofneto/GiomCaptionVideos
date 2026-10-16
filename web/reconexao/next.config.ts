import type { NextConfig } from "next";

// CSP (com nonce) é montada no proxy — aqui ficam os headers estáticos. Tudo na página é
// sensível (status de credenciais), então no-store vale para todas as rotas.
const config: NextConfig = {
  poweredByHeader: false,
  reactStrictMode: true,
  async headers() {
    return [
      {
        source: "/:path*",
        headers: [
          { key: "X-Frame-Options", value: "DENY" },
          // Não usar no-referrer: o navegador passa a mandar "Origin: null" nos POSTs e a
          // checagem estrita de CSRF (Origin == publicOrigin) rejeita todo login válido.
          { key: "Referrer-Policy", value: "same-origin" },
          { key: "X-Content-Type-Options", value: "nosniff" },
          { key: "Cache-Control", value: "no-store, max-age=0" },
          { key: "Permissions-Policy", value: "camera=(), microphone=(), geolocation=()" },
          { key: "Strict-Transport-Security", value: "max-age=63072000; includeSubDomains" },
        ],
      },
    ];
  },
};

export default config;
