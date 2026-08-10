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
# esses elos. Binário standalone do yt-dlp (sem precisar de Python) — bem mais leve que a
# alternativa (Whisper local via CLI Python + PyTorch, ~GB de dependências, nunca usado desde
# a migração pra API de transcrição da OpenAI, ver WhisperTranscricaoService).
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg curl ca-certificates \
    && curl -L https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp -o /usr/local/bin/yt-dlp \
    && chmod a+rx /usr/local/bin/yt-dlp \
    && apt-get purge -y curl \
    && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Preenchido pelo pipeline (--build-arg BUILD_ID=$(Build.BuildId)) — vira a Release do Sentry
# (ver LoggerConfigurationSentryExtensions), pra cada deploy aparecer distinto lá.
ARG BUILD_ID=local
ENV BUILD_ID=$BUILD_ID

ENTRYPOINT ["dotnet", "Giom.VideoManager.Transcricao.Worker.dll"]
