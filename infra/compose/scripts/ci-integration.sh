#!/usr/bin/env bash
# The integration stage (ADR-0022): the platform and the control plane from images on this machine, the control plane
# migrated and seeded, then the smoke test against a tenant the fleet provisions. Exits 0 when the smoke passes.
# Needs: docker, dotnet (for the smoke tool), the five images present locally at IMAGE_REPOSITORY/iron-*:IMAGE_TAG.
# Usage: IMAGE_REPOSITORY=ghcr.io/owner IMAGE_TAG=<sha> infra/compose/scripts/ci-integration.sh [tenant]
set -euo pipefail

here=$(cd "$(dirname "$0")/.." && pwd)
root=$(cd "$here/../.." && pwd)
tenant="${1:-acme}"
export IMAGE_REPOSITORY="${IMAGE_REPOSITORY:-foundation}"
export IMAGE_TAG="${IMAGE_TAG:-dev}"
operator_secret="${OPERATOR_SECRET:-ci-operator-$(head -c 24 /dev/urandom | base64 | tr -dc 'A-Za-z0-9')}"
reader_secret="${SMOKE_READER_SECRET:-ci-reader-$(head -c 24 /dev/urandom | base64 | tr -dc 'A-Za-z0-9')}"

sh "$here/scripts/init-env.sh"
set -a
# shellcheck disable=SC1091
. "$here/.env"
set +a

compose() {
  docker compose --file "$here/compose.yaml" --env-file "$here/.env" "$@"
}

echo "== platform (core + ci profile) from $IMAGE_REPOSITORY/*:$IMAGE_TAG"
compose --profile ci up --detach --wait
sh "$here/scripts/wait-ready.sh" tempo

echo "== control plane secret, migrations and seed"
CONTROL_DB_HOST=postgres CONTROL_BLOB_HOST=blob "$here/scripts/write-control-secret.sh"
compose --profile control run --rm --no-deps control-api migrate
compose --profile control run --rm --no-deps \
  -e Seed__ServiceAccounts__0__ClientId=operator -e "Seed__ServiceAccounts__0__Secret=$operator_secret" -e Seed__ServiceAccounts__0__Role=tenant-administrator -e "Seed__ServiceAccounts__0__DisplayName=Pipeline operator" \
  -e Seed__ServiceAccounts__1__ClientId=smoke-reader -e "Seed__ServiceAccounts__1__Secret=$reader_secret" -e Seed__ServiceAccounts__1__Role=service-default -e "Seed__ServiceAccounts__1__DisplayName=Smoke reader" \
  control-api seed

echo "== control plane API and fleet worker"
# Recreated every time: they are stateless, and a container left behind by a failed start (a port in use, say) keeps a
# broken network namespace that Compose would otherwise reuse.
compose --profile control up --detach --wait --force-recreate control-api fleet

echo "== smoke against tenant $tenant"
cd "$root"
SMOKE_CONTROL_URL=http://localhost:5200 SMOKE_OPERATOR_SECRET="$operator_secret" SMOKE_READER_SECRET="$reader_secret" \
  SMOKE_TENANT="$tenant" SMOKE_STORE_TOKEN="$OPENBAO_ROOT_TOKEN" SMOKE_REPORT="${SMOKE_REPORT:-smoke-report.json}" \
  dotnet run --project tools/Iron.Smoke --configuration Release
