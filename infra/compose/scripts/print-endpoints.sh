#!/bin/sh
# Prints where to find the platform services after `make up` or `make up-slim`.
set -eu
here=$(cd "$(dirname "$0")/.." && pwd)
# shellcheck disable=SC1091
. "$here/.env"
mode=${1:-full}

cat <<TEXT

Platform is up ($mode profile).

  Traefik dashboard    http://traefik.localhost
  PostgreSQL           localhost:5432          user $POSTGRES_SUPERUSER (password in infra/compose/.env)
  Blob storage         http://localhost:10000/$BLOB_ACCOUNT_NAME   (Azurite; account key in infra/compose/.env)
  OpenBao              http://localhost:8200   or http://openbao.localhost (root token in infra/compose/.env)
TEXT

if [ "$mode" = "full" ]; then
cat <<TEXT
  Grafana              http://grafana.localhost   (viewer without login; admin password in infra/compose/.env)
  Prometheus           http://prometheus.localhost
  OTLP from the host   localhost:4317 (gRPC)   localhost:4318 (HTTP)
TEXT
fi

cat <<TEXT

Browsers resolve *.localhost by themselves. Command line tools need the line printed by: make hosts
Stop with: make down   (data is kept)   or   make reset   (data is deleted)

TEXT
