# syntax=docker/dockerfile:1
# Build from the repository root: docker build -f src/Hosts/Iron.Host.Fleet/Dockerfile -t foundation/iron-fleet:dev .
# Every image here by its version and its digest, which Dependabot keeps current (ADR-0055).

# The Docker CLI and its Compose plugin, a stage of its own so Dependabot sees the image. An exact CLI release, at least
# two weeks old (ADR-0003): docker:28-cli carried a Go library and a gRPC with critical vulnerabilities. Its Go findings
# wait in security/exceptions.yaml: 29.9.2 fixes four once it is two weeks old, and no Compose release the fifth yet.
FROM docker:29.8.1-cli@sha256:018edbc908e08fcc9dbf029c812c34251e9b4719e6f71ca0e5eae2a987d014ca AS docker-cli

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Hosts/Iron.Host.Fleet/Iron.Host.Fleet.csproj --configuration Release --output /app /p:UseAppHost=false

# The fleet drives Docker on the laptop (ADR-0019), so its image carries the Docker CLI and the Compose plugin, taken
# from the official docker image, and runs as root to use the mounted socket. Not chiseled, unlike the other hosts.
# The root user is the one exception to non-root images, recorded in security/exceptions.yaml (ADR-0055).
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee AS runtime
COPY --from=docker-cli /usr/local/bin/docker /usr/local/bin/docker
COPY --from=docker-cli /usr/local/libexec/docker/cli-plugins/docker-compose /usr/local/libexec/docker/cli-plugins/docker-compose
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
