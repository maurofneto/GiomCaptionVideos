# Build context: raiz do repositório (docker build -f src/Giom.VideoManager.Orquestracao.Worker/Dockerfile .)
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY src/ src/
RUN dotnet restore "src/Giom.VideoManager.Orquestracao.Worker/Giom.VideoManager.Orquestracao.Worker.csproj"

RUN dotnet publish "src/Giom.VideoManager.Orquestracao.Worker/Giom.VideoManager.Orquestracao.Worker.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ARG BUILD_ID=local
ENV BUILD_ID=$BUILD_ID

ENTRYPOINT ["dotnet", "Giom.VideoManager.Orquestracao.Worker.dll"]
