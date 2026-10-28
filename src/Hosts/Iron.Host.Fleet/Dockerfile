# syntax=docker/dockerfile:1
# Build from the repository root: docker build -f src/Hosts/Iron.Host.Fleet/Dockerfile -t foundation/iron-fleet:dev .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Hosts/Iron.Host.Fleet/Iron.Host.Fleet.csproj --configuration Release --output /app /p:UseAppHost=false

# The fleet drives Docker on the laptop (ADR-0019), so its image carries the Docker CLI and the Compose plugin, taken
# from the official docker image, and runs as root to use the mounted socket. Not chiseled, unlike the other hosts.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# An exact CLI release, at least two weeks old (ADR-0003): docker:28-cli carried a Go library and a gRPC with critical
# vulnerabilities. The root user is the one exception to non-root images, recorded in security/exceptions.yaml with
# the Go findings of the CLI and its Compose plugin that no newer release fixes yet (ADR-0055).
COPY --from=docker:29.8.1-cli /usr/local/bin/docker /usr/local/bin/docker
COPY --from=docker:29.8.1-cli /usr/local/libexec/docker/cli-plugins/docker-compose /usr/local/libexec/docker/cli-plugins/docker-compose
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /var/lib/foundation/units
# The release's version (ADR-0052), given by the release workflow; a laptop build has none and is a development build.
ARG IRON_VERSION=development
LABEL org.opencontainers.image.version=$IRON_VERSION
ENV Release__Version=$IRON_VERSION
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=6 CMD ["dotnet", "Iron.Host.Fleet.dll", "health"]
ENTRYPOINT ["dotnet", "Iron.Host.Fleet.dll"]
