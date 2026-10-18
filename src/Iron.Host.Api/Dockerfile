# syntax=docker/dockerfile:1
# Build from the repository root: docker build -f src/Iron.Host.Api/Dockerfile -t foundation/iron-api:dev .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Iron.Host.Api/Iron.Host.Api.csproj --configuration Release --output /app /p:UseAppHost=false

# Chiseled runtime: no shell, no package manager, runs as the non-root user "app" on port 8080.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=6 CMD ["dotnet", "Iron.Host.Api.dll", "health"]
ENTRYPOINT ["dotnet", "Iron.Host.Api.dll"]
