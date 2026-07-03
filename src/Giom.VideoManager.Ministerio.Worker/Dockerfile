# Build context: raiz do repositório (docker build -f src/Giom.VideoManager.Ministerio.Worker/Dockerfile .)
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# O projeto agora referencia ~15 projetos adapter (Domain/Application/Infrastructure.*) —
# copiar toda a pasta src/ é mais simples e robusto que listar cada .csproj individualmente.
COPY src/ src/
RUN dotnet restore "src/Giom.VideoManager.Ministerio.Worker/Giom.VideoManager.Ministerio.Worker.csproj"

RUN dotnet publish "src/Giom.VideoManager.Ministerio.Worker/Giom.VideoManager.Ministerio.Worker.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Giom.VideoManager.Ministerio.Worker.dll"]
