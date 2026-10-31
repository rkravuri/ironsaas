#!/usr/bin/env bash
# Measures where the transactional outbox lives (TF-085, ADR-0060): one kernel table, kernel_events.outbox, against one
# outbox table in each module's schema. Starts a throwaway PostgreSQL of the stack's version, then measures the two
# things the place changes: a change committed with its audit row and its event, by 8 clients at once (pgbench), and
# the dispatcher's poll for the events it has not handed on yet, idle and with a backlog, beside a million handed on.
# Prints the timings and removes the server. Needs Docker; takes about three minutes.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
# The stack's server, by its version and digest (infra/compose/compose.yaml).
image="postgres:19rc1@sha256:b3cb7405059fd06f9ebae4aad4780b3788dfdd2acef1ac248ad9d56ced8abb06"
name="iron-measure-outbox-$$"
seconds="${OUTBOX_SECONDS:-30}"

docker run --detach --rm --name "$name" --env POSTGRES_PASSWORD=measure --shm-size=256m "$image" >/dev/null
trap 'docker stop "$name" >/dev/null 2>&1 || true' EXIT

# The image's entrypoint starts a server to initialise the database and then restarts it: wait for the second one.
for _ in $(seq 1 90); do
  if docker logs "$name" 2>&1 | grep --quiet "init process complete" && docker exec "$name" pg_isready --username postgres --quiet; then
    break
  fi
  sleep 1
done

psql() { docker exec --interactive "$name" psql --username postgres --no-psqlrc --quiet --pset=footer=off "$@"; }

psql < "$here/outbox-place.sql"

# The writes: each transaction a business row, its audit row and its event, as a module's save is.
docker exec --interactive "$name" sh -c 'cat > /tmp/kernel.sql' <<'SQL'
BEGIN;
INSERT INTO business (id, name, version) VALUES (gen_random_uuid(), 'record', 1);
INSERT INTO audit (action, record_id, at) VALUES ('probe.record_created', gen_random_uuid(), now());
INSERT INTO kernel_events.outbox (id, type, version, record_type, record_id, occurred_at, data) VALUES (gen_random_uuid(), 'probe.record.created', 1, 'probe', gen_random_uuid(), now(), '{"record_id": "x", "unit_id": "y"}');
END;
SQL
docker exec --interactive "$name" sh -c 'cat > /tmp/modules.sql' <<'SQL'
\set m random(1, 10)
BEGIN;
INSERT INTO business (id, name, version) VALUES (gen_random_uuid(), 'record', 1);
INSERT INTO audit (action, record_id, at) VALUES ('probe.record_created', gen_random_uuid(), now());
INSERT INTO mod_:m.outbox (id, type, version, record_type, record_id, occurred_at, data) VALUES (gen_random_uuid(), 'probe.record.created', 1, 'probe', gen_random_uuid(), now(), '{"record_id": "x", "unit_id": "y"}');
END;
SQL

# Twice each, alternating, so that neither gains from running second on a warmed server.
for round in 1 2; do
  for script in kernel modules; do
    psql -c "truncate business, audit, kernel_events.outbox; select truncate_modules(); checkpoint;" >/dev/null
    echo "Writes, round $round, $script (one kernel table, or a table per module), 8 clients for ${seconds} s"
    docker exec "$name" pgbench --username postgres --no-vacuum --client 8 --jobs 4 --time "$seconds" --file "/tmp/$script.sql" postgres \
      | grep -E "^(latency average|tps)"
  done
done

psql -c "select fill_backlogs();" >/dev/null
psql -c "select * from poll_timings();"
