#!/usr/bin/env bash
# Measures how long the unit's worker takes to hand a domain event to its subscribers (TF-085 AC1, ADR-0060): for every
# delivery a tenant's subscribers handled at their first attempt, the time from the event to its handling, by the unit's
# own clock, as the median, the 95th percentile and the longest, per subscriber and over all. Reads the tenant's
# database in the laptop's PostgreSQL; run make smoke and make e2e on the tenant first, so there are events to read.
# Usage: tools/measurements/dispatch-latency.sh <tenant>
set -euo pipefail

tenant="${1:?usage: dispatch-latency.sh <tenant>}"
here="$(cd "$(dirname "$0")" && pwd)"
# The stack's superuser, from the laptop's environment file (make env writes it).
superuser="$(grep '^POSTGRES_SUPERUSER=' "$here/../../infra/compose/.env" | cut -d= -f2-)"
database="$(docker exec foundation-postgres-1 psql --username "$superuser" --dbname postgres --no-psqlrc --tuples-only --no-align \
  --command "select datname from pg_database where datname in ('$tenant', 'tenant_$tenant') order by length(datname) desc limit 1")"
if [ -z "$database" ]; then
  echo "No database for the tenant '$tenant' in the laptop's PostgreSQL." >&2
  exit 1
fi

docker exec --interactive foundation-postgres-1 psql --username "$superuser" --dbname "$database" --no-psqlrc --pset=footer=off <<'SQL'
\echo 'Handed on at the first attempt, from the time of the event to its handling, in milliseconds:'
with delivered as (
  select i.subscriber, extract(epoch from (i.handled_at - o.occurred_at)) * 1000 as ms
  from kernel_events.inbox i join kernel_events.outbox o on o.id = i.event_id
  where i.status = 'handled' and i.attempts = 1 and i.replays = 0
)
select coalesce(subscriber, 'all') as subscription, count(*) as deliveries,
       round(percentile_cont(0.5) within group (order by ms)::numeric) as median,
       round(percentile_cont(0.95) within group (order by ms)::numeric) as p95,
       round(max(ms)::numeric) as longest
from delivered group by rollup (subscriber) order by grouping(subscriber), subscriber;
SQL
