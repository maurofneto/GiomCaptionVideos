# Build context: raiz do repositório (docker build -f src/Giom.VideoManager.Transcricao.Worker/Dockerfile .)
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY src/ src/
RUN dotnet restore "src/Giom.VideoManager.Transcricao.Worker/Giom.VideoManager.Transcricao.Worker.csproj"

RUN dotnet publish "src/Giom.VideoManager.Transcricao.Worker/Giom.VideoManager.Transcricao.Worker.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# Bug #134 — YtDlpCaptionService e WhisperTranscricaoService (elos de fallback de legenda)
# dependem do binário yt-dlp (baixa legenda/áudio) + ffmpeg (yt-dlp usa pra extrair/converter
# áudio) — nenhum dos dois nunca esteve nesta imagem. Isso nunca foi percebido em produção
# porque a cadeia de fallback tinha um bug separado (Bug #134) que nunca deixava chegar até
# esses elos.
#
# Bug #137 — o asset "yt-dlp" do release (usado antes) NÃO é o binário standalone: é um script
# Python zipapp que exige `python3` no PATH (shebang `#!/usr/bin/env python3`) — não documentado
# claramente na página de release, fácil de confundir com o binário de verdade. Sem Python nesta
# imagem, toda chamada falhava com "env: 'python3': No such file or directory" (exit 127),
# derrubando as 3 camadas de fallback de transcrição em cascata (YtDlpCaption + Whisper, que
# depende do yt-dlp pra baixar áudio). O asset CERTO pra binário standalone (sem precisar de
# Python) é "yt-dlp_linux" — confirmado baixando os dois assets e comparando os magic bytes
# antes de trocar aqui: "yt-dlp" começa com o shebang Python (`#!/usr/bin/env python3` + um
# zip PK embutido), "yt-dlp_linux" é um ELF de verdade (`7f 45 4c 46`), ~40MB. `yt-dlp --version`
# no fim do RUN faz o build FALHAR se o binário baixado não rodar — fail-fast, não silencioso.
#
# "Sign in to confirm you're not a bot" (DEV, mesmo com cookies novos por canal): versões
# fixadas (não mais releases/latest) pra saber exatamente o que roda — o boot loga
# `yt-dlp --version`/`deno --version` (YtDlpDiagnosticoBoot). Pra atualizar: trocar versão E
# SHA-256 juntos (o sha256sum -c faz o build falhar se não baterem).
#   - yt-dlp: https://github.com/yt-dlp/yt-dlp/releases (hash em SHA2-256SUMS do release). O
#     yt-dlp_linux (PyInstaller) já embute o yt-dlp-ejs (0.8.0 nesta versão) — não instalar à parte.
#   - deno: https://github.com/denoland/deno/releases (hash em deno-x86_64-unknown-linux-gnu.zip.sha256sum).
#     Runtime JS que o yt-dlp-ejs usa pro n-challenge/assinatura; sem ele o yt-dlp avisa
#     "No supported JavaScript runtime could be found" e a extração do YouTube degrada.
#   - bgutil-ytdlp-pot-provider: https://github.com/Brainicism/bgutil-ytdlp-pot-provider/releases
#     (plugin yt-dlp de PO token; o zip é carregado direto de /etc/yt-dlp/plugins/). Só é usado com
#     YtDlp:PoToken:Habilitado + ProviderUrl — desligado, o worker roda o yt-dlp com
#     YTDLP_NO_PLUGINS=1. O SERVIDOR do provider (porta 4416) não está nesta imagem.
ARG YTDLP_VERSION=2026.08.19
ARG YTDLP_SHA256=58162f9bfdc27458ea47bfcb311cf47028f17d8154a8bf7d689861d46399230a
ARG DENO_VERSION=2.9.7
ARG DENO_SHA256=c6527f24f4b16031d3ae4fa9f658d5f11534c8d84ce7dc8502420280919c3490
ARG BGUTIL_POT_PROVIDER_VERSION=2.0.2
ARG BGUTIL_POT_PROVIDER_SHA256=a26b980bf6d8e8400d8f21445daa4d8381bf6183d20f0be309391f86b31e27ef
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg curl ca-certificates unzip \
    && curl -fL "https://github.com/yt-dlp/yt-dlp/releases/download/${YTDLP_VERSION}/yt-dlp_linux" -o /usr/local/bin/yt-dlp \
    && echo "${YTDLP_SHA256}  /usr/local/bin/yt-dlp" | sha256sum -c - \
    && chmod a+rx /usr/local/bin/yt-dlp \
    && curl -fL "https://github.com/denoland/deno/releases/download/v${DENO_VERSION}/deno-x86_64-unknown-linux-gnu.zip" -o /tmp/deno.zip \
    && echo "${DENO_SHA256}  /tmp/deno.zip" | sha256sum -c - \
    && unzip -o /tmp/deno.zip deno -d /usr/local/bin \
    && chmod a+rx /usr/local/bin/deno \
    && rm /tmp/deno.zip \
    && mkdir -p /etc/yt-dlp/plugins \
    && curl -fL "https://github.com/Brainicism/bgutil-ytdlp-pot-provider/releases/download/${BGUTIL_POT_PROVIDER_VERSION}/bgutil-ytdlp-pot-provider.zip" -o /etc/yt-dlp/plugins/bgutil-ytdlp-pot-provider.zip \
    && echo "${BGUTIL_POT_PROVIDER_SHA256}  /etc/yt-dlp/plugins/bgutil-ytdlp-pot-provider.zip" | sha256sum -c - \
    && /usr/local/bin/yt-dlp --version \
    && /usr/local/bin/deno --version \
    && apt-get purge -y curl unzip \
    && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*

# Deno sem checagem de update em runtime e com cache em /tmp (mesmo diretório temporário do Whisper).
ENV DENO_NO_UPDATE_CHECK=1 \
    DENO_DIR=/tmp/deno

COPY --from=build /app/publish .

# Preenchido pelo pipeline (--build-arg BUILD_ID=$(Build.BuildId)) — vira a Release do Sentry
# (ver LoggerConfigurationSentryExtensions), pra cada deploy aparecer distinto lá.
ARG BUILD_ID=local
ENV BUILD_ID=$BUILD_ID

ENTRYPOINT ["dotnet", "Giom.VideoManager.Transcricao.Worker.dll"]
