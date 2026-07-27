# Build context: raiz do repositório (docker build -f src/Giom.VideoManager.Transcricao.Worker/Dockerfile .)
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY src/ src/
RUN dotnet restore "src/Giom.VideoManager.Transcricao.Worker/Giom.VideoManager.Transcricao.Worker.csproj"

RUN dotnet publish "src/Giom.VideoManager.Transcricao.Worker/Giom.VideoManager.Transcricao.Worker.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Preenchido pelo pipeline (--build-arg BUILD_ID=$(Build.BuildId)) — vira a Release do Sentry
# (ver LoggerConfigurationSentryExtensions), pra cada deploy aparecer distinto lá.
ARG BUILD_ID=local
ENV BUILD_ID=$BUILD_ID

ENTRYPOINT ["dotnet", "Giom.VideoManager.Transcricao.Worker.dll"]
