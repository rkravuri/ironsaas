# syntax=docker/dockerfile:1
# Build from the repository root: docker build -f src/Iron.Host.Fleet/Dockerfile -t foundation/iron-fleet:dev .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Iron.Host.Fleet/Iron.Host.Fleet.csproj --configuration Release --output /app /p:UseAppHost=false

# Chiseled runtime: no shell, no package manager, runs as the non-root user "app" on port 8080.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=6 CMD ["dotnet", "Iron.Host.Fleet.dll", "health"]
ENTRYPOINT ["dotnet", "Iron.Host.Fleet.dll"]
