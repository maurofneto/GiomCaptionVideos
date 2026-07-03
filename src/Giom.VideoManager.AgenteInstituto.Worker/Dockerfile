# Build context: raiz do repositório (docker build -f src/Giom.VideoManager.AgenteInstituto.Worker/Dockerfile .)
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# O projeto referencia vários projetos adapter (Domain/Application/Infrastructure.*) —
# copiar toda a pasta src/ é mais simples e robusto que listar cada .csproj individualmente.
COPY src/ src/
RUN dotnet restore "src/Giom.VideoManager.AgenteInstituto.Worker/Giom.VideoManager.AgenteInstituto.Worker.csproj"

RUN dotnet publish "src/Giom.VideoManager.AgenteInstituto.Worker/Giom.VideoManager.AgenteInstituto.Worker.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Giom.VideoManager.AgenteInstituto.Worker.dll"]
