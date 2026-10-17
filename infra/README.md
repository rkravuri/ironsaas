# Infrastructure

- `compose/`: the laptop stack (T2, runbook in `docs/guides/local-stack.md`). Platform services (Traefik, PostgreSQL 19, Azurite as the Azure Blob stand-in, OpenBao, the telemetry stack), the control plane containers, and the template from which the fleet worker renders one Compose project per tenant.
- `kubernetes/` and OpenTofu for Azure arrive when the laptop stack has proved the design (PRD section 3.3, decision D-14).

Nothing in `infra/` holds a secret. Generated per-tenant files and local certificates are ignored by git.
