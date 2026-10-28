# Threat model: entitlements, flags and settings

- **Modules:** entitlements, flags, settings
- **Endpoints:** none
- **Reviewed:** 2026-10-28 (S3 T10a)

## What it protects

What a tenant has bought (modules, features, seats), the flags and kill switches that change behaviour, and the typed
settings, the security settings among them (passwords and lockout, MFA, sessions, signing, four-eyes, limits).

## Who can reach it

Operators, who set plans, entitlements, global flags and kill switches through the control plane; a tenant's
administrators, who change the tenant's settings; every signed-in person, whose context the flags and entitlements
shape.

## Threats

| Category | Threat | Controls | Requirements |
|---|---|---|---|
| Spoofing | A tenant claiming modules it has not bought. | Entitlements come from the control plane through the store, which the unit reads and cannot write; the API checks them as well as the shell. | TF-040, TF-043 |
| Tampering | A setting outside its rule, or a weak security setting. | Every setting is typed with its scopes and rule and refused outside them; the security settings sit in four-eyes areas, and changing four-eyes itself waits for a second administrator. | TF-057, TF-068 |
| Repudiation | Denying a change of plan, flag or setting. | Entitlement, flag and setting changes are audited with their history. | TF-045, TF-053, TF-058 |
| Information disclosure | A setting's value shown to the wrong person. | Only the settings declared for exposure reach products and the shell; a secret is never a setting. | TF-059 |
| Denial of service | A tenant switching off what others need. | Kill switches are the operator's only, and a false rule is refused. | TF-051 |
| Elevation of privilege | Raising one's own limits or bounds. | Tenants move limits only within the operator's bounds; plan changes are the operator's. | TF-042, TF-072 |

## What remains

- Settings have no record scope and no operator-wide override of a default (ADR-0036).
- Each unit reads entitlements and flags twice a minute; a revoked entitlement takes up to 30 seconds.
