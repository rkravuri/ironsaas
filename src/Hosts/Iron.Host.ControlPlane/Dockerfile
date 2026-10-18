# syntax=docker/dockerfile:1
# Build from the repository root: docker build -f src/Hosts/Iron.Host.ControlPlane/Dockerfile -t foundation/iron-control-plane:dev .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Hosts/Iron.Host.ControlPlane/Iron.Host.ControlPlane.csproj --configuration Release --output /app /p:UseAppHost=false

# Chiseled runtime: no shell, no package manager, runs as the non-root user "app" on port 8080.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=6 CMD ["dotnet", "Iron.Host.ControlPlane.dll", "health"]
ENTRYPOINT ["dotnet", "Iron.Host.ControlPlane.dll"]
