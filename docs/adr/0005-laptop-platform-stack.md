# ADR-0005: The laptop platform stack on Compose

**Status:** Accepted
**Date:** 2026-10-18
**Decided by:** Product owner and architect

## Context

PRD decision D-14: everything is built and run on a developer laptop until the team has enough confidence; Azure comes later with the same images. Section 3.3 names the local stack: containers with Compose for PostgreSQL, object storage, secrets, the telemetry stack, the control plane, the fleet orchestrator in single-node mode, tenant units and the Supplier Network. One command starts it; one command provisions a tenant. TF-190 asks for a slim profile without the telemetry stack.

## Decision

- One Compose project `foundation` in `infra/compose/`, with one Docker network `foundation` that tenant units (separate Compose projects, T14) join as an external network.
- Components and licences, all pinned to exact image versions: Traefik v3.7.12 (MIT) as the ingress; PostgreSQL 19 (PostgreSQL licence); Azurite 3.37.0 (MIT) as the stand-in for Azure Blob Storage (ADR-0006); OpenBao 2.7.1 (MPL-2.0) in development mode; OpenTelemetry Collector contrib 0.162.0 (Apache-2.0); Tempo 3.1.0, Loki 3.7.8 and Grafana 13.2.3 (AGPL-3.0); Prometheus 3.15.0 (Apache-2.0).
- Profiles: no profile for the core (ingress, database, storage, secrets); `telemetry` for the collector, Tempo, Loki, Prometheus and Grafana; `ci` for the collector and Tempo only. `make up` starts core plus telemetry, `make up-slim` the core, `make up-ci` what the pipeline needs.
- Hostnames: every web surface is a `*.localhost` name routed by Traefik from container labels (`grafana.localhost`, `traefik.localhost`, `prometheus.localhost`, `openbao.localhost`, later `control.localhost` and one name per tenant). Browsers resolve these names without configuration. Ports for host-run development are published directly: 5432, 10000, 8200, 4317 and 4318.
- TLS is optional on the laptop. `make certs` creates a certificate with mkcert; without it Traefik serves https with a self-signed certificate and http keeps working. Production always terminates TLS at the edge.
- Credentials of the platform are generated once into the git-ignored `infra/compose/.env` by `make env`. No credential lives in `compose.yaml` or anywhere else in the repository (PRD P5).
- Data lives in named volumes and survives `make down`; `make reset` deletes it.
- `make up` waits for a usable platform: Traefik, PostgreSQL, Azurite, OpenBao, Prometheus and Grafana have Compose health checks and the collector image carries its own. Tempo and Loki ship as distroless images without a shell, so no check can run inside them; a small script polls their readiness endpoints instead.
- Grafana is provisioned with the three data sources (Prometheus, Loki, Tempo, linked for trace-to-logs and exemplars) and the dashboard `Foundation requests`.

## Options considered

- A local Kubernetes (k3d or kind) from day one. Rejected for now: closest to Azure, but heavier on the laptop and harder for a new developer; the fleet runtime interface keeps the door open (ADR-0002).
- Running the platform services as Windows programs. Rejected: the runtime is Linux (P11).
- PostgreSQL 18 until 19 is generally available. Rejected: the product owner decided on 19; the RC image is fine on a laptop and the tag changes once at GA.

## Consequences

- Easier: `make up` gives every developer the same platform in a few minutes; the pipeline reuses the same file.
- Harder: OpenBao in development mode forgets its data when it stops, so tenants must be provisioned again after a restart; T12 revisits this.
- Revisit: the OpenBao mode (T12), Traefik and tenant routing when tenant units arrive (T14), resource limits when the laptop runs several units (T14).
