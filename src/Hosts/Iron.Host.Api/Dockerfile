# syntax=docker/dockerfile:1
# Build from the repository root: docker build -f src/Hosts/Iron.Host.Api/Dockerfile -t foundation/iron-api:dev .
# Every image here by its version and its digest, which Dependabot keeps current (ADR-0055).

# The location database of the sessions' city and country (ADR-0049): DB-IP's IP to City Lite, CC BY 4.0 (docs/licences.md),
# one pinned month whose checksum the build checks, so the image is repeatable and the unit fetches nothing at runtime.
# DB-IP keeps a few months online: when the pinned one goes, move both arguments to a newer month (the runbook says how).
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS locations
ARG LOCATIONS_MONTH=2026-10
ARG LOCATIONS_SHA256=c839d407f85a1e0bddba354cef16b2c398182d1c34ed8d985555dce134b36b14
RUN curl -fsSL "https://download.db-ip.com/free/dbip-city-lite-${LOCATIONS_MONTH}.mmdb.gz" -o /tmp/locations.mmdb.gz \
 && echo "${LOCATIONS_SHA256}  /tmp/locations.mmdb.gz" | sha256sum -c - \
 && mkdir -p /locations \
 && gunzip -c /tmp/locations.mmdb.gz > /locations/dbip-city-lite.mmdb \
 && rm /tmp/locations.mmdb.gz

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Hosts/Iron.Host.Api/Iron.Host.Api.csproj --configuration Release --output /app /p:UseAppHost=false

# Chiseled runtime: no shell, no package manager, runs as the non-root user "app" on port 8080.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:c89a6a61f494f20a5ae302990e814627a8d91b332fd4a8e7c1707c2d4da9555d AS runtime
WORKDIR /app
COPY --from=build /app .
COPY --from=locations /locations ./locations
# The release's version (ADR-0052), given by the release workflow; a laptop build has none and is a development build.
ARG IRON_VERSION=development
LABEL org.opencontainers.image.version=$IRON_VERSION
ENV Release__Version=$IRON_VERSION
# The base image's own non-root user, said here so the image's scan and its readers see it.
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=6 CMD ["dotnet", "Iron.Host.Api.dll", "health"]
ENTRYPOINT ["dotnet", "Iron.Host.Api.dll"]
