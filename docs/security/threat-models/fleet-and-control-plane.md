# Threat model: the fleet and the control plane

- **Modules:** fleet, tenancy
- **Endpoints:** `ANY /health/live`, `ANY /health/ready`
- **Reviewed:** 2026-10-28 (S3 T10a)

## What it protects

The platform's control: the tenant registry, provisioning and updates, every unit's secret and database credentials,
the keys' administration, the operators' actions, and the control plane's own sealed trail.

## Who can reach it

Operators, through the control plane's API with their tokens and permissions; units, as their own client, for anchors
and verifications; the fleet worker, which has no API but health; anyone who reaches a host, for its health endpoints.

## Threats

| Category | Threat | Controls | Requirements |
|---|---|---|---|
| Spoofing | A stolen operator token. | Client credentials and short-lived tokens; every critical operation needs a fresh token, a reason, and for break-glass a second operator. | TF-181, TF-026 |
| Tampering | Changing the control plane's records or trail. | The API runs as `control_app`, migrations as `control_owner`; its own chain is sealed and anchored in a container it may only add to, verified daily by `control_audit`. | TF-061, TF-181 |
| Tampering | Running an image that is not the release. | Images by digest in the installation record, checked against the code; every image scanned and its bill of materials kept with the release. | TF-218, TF-076 |
| Repudiation | An operator denying an action. | Operator actions are audited in the control plane and, for a tenant, in its own trail. | TF-180, TF-181 |
| Information disclosure | The API reading the fleet's administrative secrets. | The API and the fleet have separate secrets, policies and store tokens; a test proves the API's token cannot read the fleet's secret. Links never appear in job output or logs. | TF-073 |
| Information disclosure | Health endpoints telling too much. | They answer a status, no configuration or versions. | TF-133 |
| Denial of service | Floods against the control plane. | The API's rate limits; the fleet listens to nothing but health. | TF-072 |
| Elevation of privilege | The fleet's Docker socket. | On the laptop and the runner the fleet drives Docker as root through the mounted socket; recorded as DS-0002 in `security/exceptions.yaml`; units' tokens reach only their own tenant's paths and keys. | TF-005, TF-073 |

## What remains

- The Docker socket until Kubernetes in S6, and the fleet image's Go findings in its Docker CLI and Compose plugin
  until a fixed release is two weeks old: both in `security/exceptions.yaml`.
- The control plane's own keys and database password are made but not rotated yet (S6).
- Pages for verifications and anchors wait for the operator console (S6).
