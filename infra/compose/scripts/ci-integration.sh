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

echo "== the store: initialised on a new runner, unsealed by its key (ADR-0028)"
bash "$here/scripts/init-store.sh"
# The root token was written into .env just now; exported variables win over --env-file, so read the file again.
set -a
# shellcheck disable=SC1091
. "$here/.env"
set +a

echo "== the fleet's bootstrap secret; the control plane's own roles, anchors, keys, secrets and tokens (ADR-0053, ADR-0054)"
CONTROL_DB_HOST=postgres CONTROL_BLOB_HOST=blob "$here/scripts/write-control-secret.sh"
compose --profile control run --rm --no-deps -e "Secrets__Token=$OPENBAO_ROOT_TOKEN" -e Secrets__Path=fleet fleet prepare-control
"$here/scripts/write-control-tokens.sh"
set -a
# shellcheck disable=SC1091
. "$here/.env"
set +a

echo "== migrations and seed as the control plane's owner, with the fleet's token, which reads the migrate secret"
compose --profile control run --rm --no-deps -e "Secrets__Token=$FLEET_STORE_TOKEN" -e Secrets__Path=control-migrate control-api migrate
compose --profile control run --rm --no-deps -e "Secrets__Token=$FLEET_STORE_TOKEN" -e Secrets__Path=control-migrate \
  -e Seed__ServiceAccounts__0__ClientId=operator -e "Seed__ServiceAccounts__0__Secret=$operator_secret" -e Seed__ServiceAccounts__0__Role=tenant-administrator -e "Seed__ServiceAccounts__0__DisplayName=Pipeline operator" \
  -e Seed__ServiceAccounts__1__ClientId=smoke-reader -e "Seed__ServiceAccounts__1__Secret=$reader_secret" -e Seed__ServiceAccounts__1__Role=service-default -e "Seed__ServiceAccounts__1__DisplayName=Smoke reader" \
  -e Seed__ServiceAccounts__2__ClientId=operator-two -e "Seed__ServiceAccounts__2__Secret=$operator_secret-two" -e Seed__ServiceAccounts__2__Role=tenant-administrator -e "Seed__ServiceAccounts__2__DisplayName=Pipeline second operator" \
  control-api seed

echo "== control plane API and fleet worker"
# Recreated every time: they are stateless, and a container left behind by a failed start (a port in use, say) keeps a
# broken network namespace that Compose would otherwise reuse.
compose --profile control up --detach --wait --force-recreate control-api fleet

echo "== the single sign-on test provider (ADR-0048), for the smoke's and the browser tests' sign-ins through it"
compose --profile sso up --detach --wait idp
sh "$here/scripts/wait-ready.sh" idp

echo "== smoke against tenant $tenant"
cd "$root"
# The stage's tenant runs the sample module (ADR-0057), so the smoke's and the browser tests' record steps have records.
SMOKE_CONTROL_URL=http://localhost:5200 SMOKE_OPERATOR_SECRET="$operator_secret" SMOKE_READER_SECRET="$reader_secret" \
  SMOKE_TENANT="$tenant" SMOKE_STORE_TOKEN="$OPENBAO_ROOT_TOKEN" SMOKE_REPORT="${SMOKE_REPORT:-smoke-report.json}" SMOKE_SAMPLE_RECORDS=1 \
  dotnet run --project tools/Iron.Smoke --configuration Release
