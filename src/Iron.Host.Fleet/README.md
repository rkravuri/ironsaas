# Iron.Host.Fleet

The fleet orchestrator: a worker of the control plane that creates, updates and removes tenant units (S3). Bound to the `control` tenant.

A host composes modules; it holds no business logic of its own. Modules are listed explicitly in `Program.cs` (no assembly scanning), so a reader can see what runs in this process.

## Configuration

| Key | Meaning |
|---|---|
| `Tenant:Id` | The tenant slug this unit is bound to. Required. |
| `Tenant:Hostnames` | The hostnames this unit answers to. Required, at least one. Other hostnames get 421. |
| `ConnectionStrings:Database` | The unit's database. Required (T12 moves it to the secret store). |
| `Security:*` | See Iron.Security. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | The collector; default `http://localhost:4317`. |
| `Heartbeat:Interval` | Heartbeat interval, default `00:01:00`. |

## Commands

The image answers these besides serving (ADR-0011):

| Command | Does |
|---|---|
| `migrate` | Applies the pending migrations of every module context, each with its own history table in its schema. |
| `seed` | Runs every registered `ISeeder`, lowest order first. |
| `schema-check` | Checks the unit's schemas against TF-080; exit code 1 on violations. |
| `health [url]` | Asks `/health/ready` (default `http://localhost:8080/health/ready`) and exits 0 on 200. The image's HEALTHCHECK. |

Example: `docker run --rm --env-file unit.env foundation/iron-fleet:dev migrate`.

## Image

`docker build -f src/Iron.Host.Fleet/Dockerfile -t foundation/iron-fleet:dev .` from the repository root, or `make images` for all four. Runtime image `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled`: no shell, non-root, port 8080.
