# syntax=docker/dockerfile:1
# Build from the repository root: docker build -f src/Hosts/Iron.Host.Worker/Dockerfile -t foundation/iron-worker:dev .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Hosts/Iron.Host.Worker/Iron.Host.Worker.csproj --configuration Release --output /app /p:UseAppHost=false

# Chiseled runtime: no shell, no package manager, runs as the non-root user "app" on port 8080.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app .
# The base image's own non-root user, said here so the image's scan and its readers see it.
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=6 CMD ["dotnet", "Iron.Host.Worker.dll", "health"]
ENTRYPOINT ["dotnet", "Iron.Host.Worker.dll"]
