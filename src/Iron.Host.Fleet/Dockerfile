# syntax=docker/dockerfile:1
# Build from the repository root: docker build -f src/Iron.Host.Fleet/Dockerfile -t foundation/iron-fleet:dev .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Iron.Host.Fleet/Iron.Host.Fleet.csproj --configuration Release --output /app /p:UseAppHost=false

# The fleet drives Docker on the laptop (ADR-0019), so its image carries the Docker CLI and the Compose plugin, taken
# from the official docker image, and runs as root to use the mounted socket. Not chiseled, unlike the other hosts.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
COPY --from=docker:28-cli /usr/local/bin/docker /usr/local/bin/docker
COPY --from=docker:28-cli /usr/local/libexec/docker/cli-plugins/docker-compose /usr/local/libexec/docker/cli-plugins/docker-compose
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /var/lib/foundation/units
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=6 CMD ["dotnet", "Iron.Host.Fleet.dll", "health"]
ENTRYPOINT ["dotnet", "Iron.Host.Fleet.dll"]
