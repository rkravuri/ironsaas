# Runbook: the laptop platform stack

Everything Foundation needs runs on the developer laptop in containers (PRD section 3.3, decision D-14). This runbook covers starting, stopping, reaching and resetting the platform. Tenant units come with T14.

## Prerequisites

- WSL 2 with Ubuntu, and Docker. Either Docker Engine installed inside WSL (`sudo apt install docker.io docker-compose-v2`, then add your user to the `docker` group and log in again) or Docker Desktop with WSL integration switched on for your distribution.
- `docker compose version` prints a 2.x version.
- Ports 80, 443, 5432, 8200, 10000, 4317 and 4318 are free on the laptop.
- Memory: about 2.5 GB for the full profile, about 1 GB for the slim profile. The reference laptop has 32 GB (PRD open point O-11).

## Commands

| Command | What it does |
|---|---|
| `make up` | Creates `infra/compose/.env` on first use, starts every platform container (full profile) and waits until all are healthy. Prints the endpoints. |
| `make up-slim` | The same without the telemetry stack. For low memory or when only the database, storage and secrets are needed. |
| `make up-ci` | Slim plus the collector and Tempo. The pipeline uses this to check traces (T18). |
| `make down` | Stops and removes the containers. Data volumes stay, so `make up` continues where you left off. |
| `make reset` | `make down` plus deletion of every data volume. Use it to start from zero. |
| `make ps`, `make logs`, `make logs SERVICE=postgres` | Status and logs. |
| `make hosts` | Prints the `/etc/hosts` line for command line tools. |
| `make certs` | Creates a locally trusted TLS certificate with mkcert (optional). |

## Where things are

| Service | Address | Credentials |
|---|---|---|
| Traefik dashboard | http://traefik.localhost | none |
| PostgreSQL 19 | `localhost:5432`, database `postgres` | `POSTGRES_SUPERUSER` and its password in `infra/compose/.env` |
| Blob storage (Azurite) | `http://localhost:10000/<account>` | `BLOB_ACCOUNT_NAME` and `BLOB_ACCOUNT_KEY` in `.env` |
| OpenBao | http://localhost:8200 and http://openbao.localhost | `OPENBAO_ROOT_TOKEN` in `.env` |
| Grafana | http://grafana.localhost | viewer without login; `admin` with `GRAFANA_ADMIN_PASSWORD` to edit |
| Prometheus | http://prometheus.localhost | none |
| Tempo API | http://tempo.localhost and `localhost:3200` | none |
| Loki API | `localhost:3100` | none |
| OTLP for applications on the host | `localhost:4317` (gRPC), `localhost:4318` (HTTP) | none |

Tempo and Loki run as distroless images without a shell, so Compose cannot health-check them; `make up` polls their readiness endpoints with `infra/compose/scripts/wait-ready.sh` before it returns.

Inside the Docker network `foundation` the services are reached by name: `postgres:5432`, `blob:10000`, `openbao:8200`, `otel-collector:4317`, `tempo:3200`, `loki:3100`, `prometheus:9090`.

Hostnames: browsers (Chrome, Edge, Firefox) resolve every `*.localhost` name to the laptop by themselves, so `http://grafana.localhost` works from Windows without any setup. Command line tools in WSL (`curl`, `psql`) do not; run `make hosts` once and add the printed line to `/etc/hosts`.

## Running a host against the platform

`make run-api` starts the tenant API host on http://localhost:5100 with `dotnet run`, bound to the tenant id `dev`, with the platform database (superuser, a laptop shortcut until T14 creates tenant roles) and the collector on `localhost:4317`. Then:

```bash
curl -i http://localhost:5100/health/ready     # the headers show X-Correlation-Id and the security headers
```

In Grafana, Explore, data source Tempo, search for service `iron-api`: the request appears as a trace with the `GET /health/ready` span and the PostgreSQL `select 1` span under it. The dashboard `Foundation requests` fills after a few requests. In Development the API browser is at http://localhost:5100/scalar/v1.

## Credentials

`make up` creates `infra/compose/.env` with random values on first use. The file is ignored by git and holds only laptop credentials. To rotate them: `make reset`, delete the file, `make up`. The values must match the data volumes, so do not delete the file without resetting the volumes.

## Known limits of the laptop stack

- OpenBao runs in development mode: in memory, unsealed, one root token. Everything stored in it is lost when its container stops, including tenant secrets written by provisioning (T14). After `make down` or a restart, provision the tenants again. T12 decides whether a persistent local mode is worth its setup cost.
- PostgreSQL is the 19 release candidate until the GA image exists (planned 2026-11-19). An RC to GA change can require `make reset` (data directory format); on a laptop that is acceptable.
- TLS is optional. Without `make certs`, Traefik answers https with a self-signed certificate; use http. With mkcert, the root certificate must also be trusted in Windows, where the browser runs (the script prints the two steps).
- Azurite implements most of the Azure Blob API, not all of it (no blob versioning, no lifecycle policies). Iron.Files keeps its own version history for that reason.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `make up` ends with `tempo did not become ready` or the same for loki | `make logs SERVICE=tempo` (or loki). Usually a configuration error after an upgrade of the pinned version. |
| `make up` hangs on a service and ends with a health timeout | `make logs SERVICE=<name>`. Most often a port is taken on the laptop; stop the other program or change the published port in `compose.yaml`. |
| `permission denied` on `/var/run/docker.sock` | Your user is not in the `docker` group. `sudo usermod -aG docker $USER`, then close and reopen the WSL terminal. |
| `bind: address already in use` for port 80 or 443 | Another web server runs on Windows or WSL (IIS, Apache, another Traefik). Stop it, or publish Traefik on other ports and use `http://grafana.localhost:8080`. |
| `grafana.localhost` opens in the browser but `curl` fails in WSL | Add the `/etc/hosts` line from `make hosts`. |
| Postgres starts but tools cannot connect | Check the user name and password in `infra/compose/.env`; connect to database `postgres`. |
| Grafana shows the dashboard but no data | Expected until T6: nothing sends OpenTelemetry metrics yet. The Traefik panel and the scrape targets panel should already show values. |
| Old credentials after recreating `.env` | The volumes still hold the old values. `make reset`, then `make up`. |

## Host images

`make images` builds `foundation/iron-api:dev`, `foundation/iron-worker:dev`, `foundation/iron-control-plane:dev` and `foundation/iron-fleet:dev` from the repository root. The first build downloads the SDK and runtime images (about 1 GB) and restores packages; later builds reuse the layers.

To run the API image by hand against the laptop database, note that inside a container `localhost` is the container itself, so the database host is `host.docker.internal` (Docker Desktop) or the Compose network name `postgres` when the container joins the platform network:

```bash
docker run --rm -p 5100:8080 --network foundation \
  -e Tenant__Id=dev -e Tenant__Hostnames__0=localhost \
  -e ConnectionStrings__Database="Host=postgres;Port=5432;Database=postgres;Username=...;Password=..." \
  -e OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317 \
  foundation/iron-api:dev
```

T14 generates these settings per tenant unit; nobody types them by hand after that.


## The unit secret (T12)

A real unit gets its configuration from OpenBao: `make secret-dev` writes `secret/tenants/dev/unit` with the connection string, the storage settings and a signing key (generated once into `infra/compose/.dev-signing-key.pem`, git-ignored), and `make run-api-secrets` starts the API host with only the store address, the root token (the fleet gives each unit its own token, T14), the path and the tenant in its environment. OpenBao runs in development mode, in memory: after `make down` and `make up`, run `make secret-dev` again.

Readiness (`/health/ready`) now reports `postgres`, `secret-store` (when configured) and `object-storage`; the storage check says "The blob container 'dev' does not exist." until `make seed` has created it.


## The control plane (T13)

The control plane is a unit of its own with its own database, `control`, in the laptop PostgreSQL. `make migrate-control` creates and migrates it, `make seed-control` seeds the system roles and the `operator` service account, and `make run-control` serves it on http://localhost:5200 (hostnames `localhost` and `control.localhost`). With it running, `make provision TENANT=acme` gets an operator token, registers `acme` with the hostname `acme.localhost` and the administrator `admin@acme.localhost`, and requests provisioning; until T14 the job stays Requested, and `GET /api/v1/tenants/acme/provisioning` shows it.
