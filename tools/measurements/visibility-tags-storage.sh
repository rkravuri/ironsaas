#!/usr/bin/env bash
# Measures where visibility tags on records are stored (TF-241, ADR-0057): one kernel table joined in the policy's query,
# against a tag column on each module's table. Starts a throwaway PostgreSQL of the stack's version, runs
# visibility-tags-storage.sql, prints the timings and removes the server. Needs Docker; takes about two minutes.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
# The stack's server, by its version and digest (infra/compose/compose.yaml).
image="postgres:19rc1@sha256:b3cb7405059fd06f9ebae4aad4780b3788dfdd2acef1ac248ad9d56ced8abb06"
name="iron-measure-visibility-tags-$$"

docker run --detach --rm --name "$name" --env POSTGRES_PASSWORD=measure --shm-size=256m "$image" >/dev/null
trap 'docker stop "$name" >/dev/null 2>&1 || true' EXIT

# The image's entrypoint starts a server to initialise the database and then restarts it: wait for the second one.
for _ in $(seq 1 90); do
  if docker logs "$name" 2>&1 | grep --quiet "init process complete" && docker exec "$name" pg_isready --username postgres --quiet; then
    break
  fi
  sleep 1
done

docker exec --interactive "$name" psql --username postgres --no-psqlrc --quiet --pset=footer=off < "$here/visibility-tags-storage.sql"
