# ADR-0019: Compose as the laptop unit runtime

Date: 2026-10-18
Status: Accepted

## Context

ADR-0002: one isolated deployment unit per tenant, Compose on the laptop and Kubernetes on Azure, from the same images. ADR-0018 put the fleet's steps behind `IUnitRuntime`. This record is the laptop implementation and the operator's loop around it.

## Decision

1. **One template, one `.env` per unit.** `unit.compose.yaml`, embedded in the fleet assembly, is the same file for every tenant: an `api` and a `worker` service from `foundation/iron-api` and `foundation/iron-worker` at the configured tag, limits from the size class, Traefik labels for the tenant's hostnames, the platform network for PostgreSQL, Azurite, OpenBao and the collector, and a network of the unit's own. The fleet writes the file and a `.env` with the unit's values into `Fleet:Units:ComposeDirectory/<slug>/` and runs `docker compose --project-name tenant-<slug> up --detach --wait --remove-orphans`. `--wait` makes Compose wait for the images' own health checks.
2. **The unit's environment is three secret settings and the tenant.** `Secrets__Address`, `Secrets__Token`, `Secrets__Mount`, `Secrets__Path`, `Tenant__Id`, `Tenant__HostnameList` and the collector address. Everything else comes from the unit's secret at start (ADR-0016). `Tenant__HostnameList` is a comma-separated form of the hostnames, because a rendered file cannot carry a configuration array of unknown length.
3. **Image commands are `docker run`.** `migrate` and `seed` run the API image on the platform network with the unit's environment plus the owner connection string, from a temporary env file that is deleted right after; the output comes back to the step, which is how the invitation link is found.
4. **Readiness is checked through the ingress** with the tenant's primary hostname as the Host header, against `Fleet:Units:IngressUrl` (`http://traefik` inside the network, `http://localhost` from the laptop), so verify proves the route as well as the unit.
5. **De-provisioning is `docker compose down`.** A de-provision job has one step: the containers and the routes go, the database, the container and the secrets stay (TF-210 in S1; the deletion job is S6). Only an Active tenant can be de-provisioned; its status becomes Decommissioned.
6. **The fleet worker image carries the Docker CLI and the Compose plugin**, copied from the official `docker:28-cli` image into the ASP.NET runtime image, and runs as root to use the mounted socket. It is the one image that is not chiseled, and it should be the only process with that socket.
7. **The laptop loop runs the control plane from source** (`make run-control`, `make run-fleet`), with the fleet driving the laptop's own Docker and writing units under `infra/compose/units/`. Running the control plane as containers is the pipeline's job (T18).

## Consequences

- `make provision TENANT=acme` is one command from registration to a unit answering on `acme.localhost`, with every step and its duration printed; a failed step is fixed and the same command resumes the job.
- Units share the platform network, so the network does not isolate them from each other in S1; the database roles, the container signatures and the unit tokens do (ADR-0018). Network policies arrive on Kubernetes (S6).
- The fleet needs the images to exist at the configured tag: `make images` on the laptop, the registry in the pipeline.
