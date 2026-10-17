# Infrastructure

- `compose/`: the laptop stack (T2). Platform services (Traefik, PostgreSQL 19, MinIO, OpenBao, the telemetry stack), the control plane containers, and the template from which the fleet worker renders one Compose project per tenant.
- `kubernetes/` and OpenTofu for Azure arrive when the laptop stack has proved the design (PRD section 3.3, decision D-14).

Nothing in `infra/` holds a secret. Generated per-tenant files and local certificates are ignored by git.
