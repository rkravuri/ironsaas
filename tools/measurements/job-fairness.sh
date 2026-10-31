#!/usr/bin/env bash
# Measures fairness between two tenants' units on the laptop (TF-122 AC1, ADR-0061): the busy tenant's unit starts the
# sample module's samples.saturate, which queues 10,000 bulk runs that each hold a database connection and a processor,
# eight at once; while they run, the quiet tenant's job is started three times, a minute apart, through the operator's
# path, and the script reports how long each waited from queued to started in the quiet unit, and every run of the
# quiet unit that fell due meanwhile, beside the busy unit's connections, processors and runs. The busy tenant must run
# the sample module (echo does on the laptop); the quiet one needs nothing. The backlog drains by itself: its runs
# queued past five minutes from the start end at once, without work.
# Usage: tools/measurements/job-fairness.sh <busy tenant> <quiet tenant> [quiet job, default identity.token_cleanup]
set -euo pipefail

busy="${1:?usage: job-fairness.sh <busy tenant> <quiet tenant> [quiet job]}"
quiet="${2:?usage: job-fairness.sh <busy tenant> <quiet tenant> [quiet job]}"
job="${3:-identity.token_cleanup}"
here="$(cd "$(dirname "$0")" && pwd)"
root="$here/../.."
# The stack's superuser, from the laptop's environment file (make env writes it).
superuser="$(grep '^POSTGRES_SUPERUSER=' "$root/infra/compose/.env" | cut -d= -f2-)"

sql() {
  docker exec foundation-postgres-1 psql --username "$superuser" --dbname "$1" --no-psqlrc --tuples-only --no-align --command "$2"
}

database_of() {
  sql postgres "select datname from pg_database where datname in ('$1', 'tenant_$1') order by length(datname) desc limit 1"
}

busy_db="$(database_of "$busy")"
quiet_db="$(database_of "$quiet")"
if [ -z "$busy_db" ] || [ -z "$quiet_db" ]; then
  echo "No database for '$busy' or '$quiet' in the laptop's PostgreSQL." >&2
  exit 1
fi

worker_of() { docker ps --format '{{.Names}}' | grep -E "^tenant-$1-worker" | head -1; }
cpu_of() { docker stats --no-stream --format '{{.CPUPerc}}' "$1" 2>/dev/null || echo "?"; }

echo "Starting samples.saturate in $busy's unit through the fleet."
make -s -C "$root" job-run TENANT="$busy" JOB=samples.saturate REASON="Fairness measurement (TF-122 AC1): the busy tenant's backlog" > /dev/null

echo "Waiting for its backlog to hold eight runs at once."
for _ in $(seq 1 60); do
  running="$(sql "$busy_db" "select count(*) from kernel_jobs.runs where job = 'samples.saturate' and status = 'running'")"
  [ "$running" -ge 8 ] && break
  sleep 2
done
started="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
echo "Saturated from $started: $running runs of samples.saturate running in $busy."

busy_worker="$(worker_of "$busy")"
quiet_worker="$(worker_of "$quiet")"
for round in 1 2 3; do
  make -s -C "$root" job-run TENANT="$quiet" JOB="$job" REASON="Fairness measurement (TF-122 AC1): the quiet tenant's job, round $round" > /dev/null
  sleep 5
  connections="$(sql postgres "select count(*) from pg_stat_activity where usename = '${busy}_app'")"
  queued="$(sql "$busy_db" "select count(*) from kernel_jobs.runs where job = 'samples.saturate' and status = 'queued'")"
  echo "Round $round: $busy holds $connections connections, its worker at $(cpu_of "$busy_worker") of a processor, $queued saturating runs still queued; $quiet's worker at $(cpu_of "$quiet_worker")."
  [ "$round" -lt 3 ] && sleep 55
done
ended="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

echo
echo "$quiet's runs of $job started by hand while $busy was saturated, from queued to started:"
docker exec --interactive foundation-postgres-1 psql --username "$superuser" --dbname "$quiet_db" --no-psqlrc --pset=footer=off <<SQL
select to_char(due_at, 'HH24:MI:SS') as queued, to_char(started_at, 'HH24:MI:SS') as started,
       round(extract(epoch from (started_at - due_at)) * 1000) as waited_ms, status, duration_ms
from kernel_jobs.runs
where job = '$job' and reason like 'Fairness measurement%' and due_at between '$started' and '$ended'
order by due_at;
SQL

echo "Every run of $quiet that fell due meanwhile, by job, from due to started, in milliseconds:"
docker exec --interactive foundation-postgres-1 psql --username "$superuser" --dbname "$quiet_db" --no-psqlrc --pset=footer=off <<SQL
select coalesce(job, 'all') as job, count(*) as runs,
       round(percentile_cont(0.5) within group (order by extract(epoch from (started_at - due_at)) * 1000)::numeric) as median,
       round(max(extract(epoch from (started_at - due_at)) * 1000)::numeric) as longest
from kernel_jobs.runs
where started_at is not null and due_at between '$started' and '$ended'
group by rollup (job) order by grouping(job), job;
SQL

echo "$busy's backlog meanwhile:"
sql "$busy_db" "select status, count(*) from kernel_jobs.runs where job = 'samples.saturate' group by status order by status"
