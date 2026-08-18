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
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg curl ca-certificates \
    && curl -fL https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux -o /usr/local/bin/yt-dlp \
    && chmod a+rx /usr/local/bin/yt-dlp \
    && /usr/local/bin/yt-dlp --version \
    && apt-get purge -y curl \
    && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Preenchido pelo pipeline (--build-arg BUILD_ID=$(Build.BuildId)) — vira a Release do Sentry
# (ver LoggerConfigurationSentryExtensions), pra cada deploy aparecer distinto lá.
ARG BUILD_ID=local
ENV BUILD_ID=$BUILD_ID

ENTRYPOINT ["dotnet", "Giom.VideoManager.Transcricao.Worker.dll"]
