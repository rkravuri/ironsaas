#!/usr/bin/env bash
# Proves on the laptop how a unit's jobs behave across worker processes (TF-120 AC1, ADR-0061). First the tenant's
# worker is restarted, as an update does: the runs it was running go back to the queue and the new worker takes them
# within seconds. Then the unit runs two workers for five minutes: every occurrence of a scheduled job runs once, and
# both workers hold runs, sampled every 15 seconds since a finished run no longer names its worker. The unit goes back
# to one worker at the end.
# Usage: tools/measurements/job-workers.sh <tenant>
set -euo pipefail

tenant="${1:?usage: job-workers.sh <tenant>}"
here="$(cd "$(dirname "$0")" && pwd)"
superuser="$(grep '^POSTGRES_SUPERUSER=' "$here/../../infra/compose/.env" | cut -d= -f2-)"
database="$(docker exec foundation-postgres-1 psql --username "$superuser" --dbname postgres --no-psqlrc --tuples-only --no-align \
  --command "select datname from pg_database where datname in ('$tenant', 'tenant_$tenant') order by length(datname) desc limit 1")"
sql() { docker exec foundation-postgres-1 psql --username "$superuser" --dbname "$database" --no-psqlrc --pset=footer=off --command "$1"; }
rows() { docker exec foundation-postgres-1 psql --username "$superuser" --dbname "$database" --no-psqlrc --tuples-only --no-align --command "$1"; }
compose() { docker exec foundation-fleet-1 docker compose -p "tenant-$tenant" -f "/var/lib/foundation/units/$tenant/compose.yaml" "$@"; }

echo "Restarting $tenant's worker, as an update does."
stopped="$(date -u +%Y-%m-%dT%H:%M:%S.%NZ)"
docker restart "tenant-$tenant-worker-1" > /dev/null
sleep 20
echo "The runs that were running when it stopped, and when the new worker took them:"
sql "select job, to_char(scheduled_for, 'HH24:MI:SS') as occurrence, attempts, status,
            round(extract(epoch from (started_at - '$stopped'::timestamptz)) * 1000) as started_ms_after_the_stop
     from kernel_jobs.runs where scheduled_for < '$stopped' and started_at > '$stopped' order by job"

echo
echo "Running two workers for five minutes."
compose up -d --no-recreate --scale worker=2 worker > /dev/null 2>&1
from="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
samples="$(mktemp)"
for _ in $(seq 1 20); do
  rows "select split_part(holder, '/', 1) from kernel_jobs.runs where status = 'running'" >> "$samples"
  sleep 15
done
to="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
compose up -d --no-recreate --scale worker=1 worker > /dev/null 2>&1

echo "The workers holding runs, by host, over 20 samples (each a running run seen):"
sort "$samples" | uniq -c
rm -f "$samples"
echo "Scheduled occurrences due between $from and $to, per job: runs and distinct occurrences, which are equal when each ran once:"
sql "select r.job, count(*) as runs, count(distinct r.scheduled_for) as occurrences
     from kernel_jobs.runs r join kernel_jobs.jobs j on j.name = r.job
     where j.next_due_at is not null and r.scheduled_for between '$from' and '$to' group by r.job order by r.job"
