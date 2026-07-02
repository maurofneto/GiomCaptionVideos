# Build context: raiz do repositório (docker build -f src/GiomMinisterio/Dockerfile .)
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ["src/GiomMinisterio/GiomMinisterio.csproj", "src/GiomMinisterio/"]
COPY ["src/Domain/Domain.csproj", "src/Domain/"]
COPY ["src/Application/Application.csproj", "src/Application/"]
COPY ["src/Infrastructure/Infrastructure.csproj", "src/Infrastructure/"]
RUN dotnet restore "src/GiomMinisterio/GiomMinisterio.csproj"

COPY src/GiomMinisterio/ src/GiomMinisterio/
COPY src/Domain/ src/Domain/
COPY src/Application/ src/Application/
COPY src/Infrastructure/ src/Infrastructure/

RUN dotnet publish "src/GiomMinisterio/GiomMinisterio.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "GiomMinisterio.dll"]
