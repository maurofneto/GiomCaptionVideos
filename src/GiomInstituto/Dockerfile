# Build context: raiz do repositório (docker build -f src/GiomInstituto/Dockerfile .)
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ["src/GiomInstituto/GiomInstituto.csproj", "src/GiomInstituto/"]
COPY ["src/Domain/Domain.csproj", "src/Domain/"]
COPY ["src/Application/Application.csproj", "src/Application/"]
COPY ["src/Infrastructure/Infrastructure.csproj", "src/Infrastructure/"]
RUN dotnet restore "src/GiomInstituto/GiomInstituto.csproj"

COPY src/GiomInstituto/ src/GiomInstituto/
COPY src/Domain/ src/Domain/
COPY src/Application/ src/Application/
COPY src/Infrastructure/ src/Infrastructure/

RUN dotnet publish "src/GiomInstituto/GiomInstituto.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "GiomInstituto.dll"]
