# Threat model: access and authorization

- **Modules:** authz
- **Endpoints:** none
- **Reviewed:** 2026-10-28 (S3 T10a)

## What it protects

Who may do what: the permission catalogue, roles, assignments scoped to units and records, organisation units, record
policies and sharing, delegations, conflict rules and access reviews.

## Who can reach it

Every signed-in request, which the grants decide; administrators, who change roles and assignments within what they
hold; reviewers, who decide access reviews.

## Threats

| Category | Threat | Controls | Requirements |
|---|---|---|---|
| Spoofing | Acting for someone without being their delegate. | Acting is a substitution in the grants, allowed only by a delegation, and every event names the person acted for. | TF-037 |
| Tampering | Granting more than one holds. | Nobody grants more than they hold, at the same place; the conflict rules refuse or warn; four-eyes on roles and assignments where the tenant lists them. | TF-033, TF-036, TF-068 |
| Repudiation | Denying a grant or a removal. | Grants, removals and role changes are audited with their field history. | TF-062, TF-033 |
| Information disclosure | Reading outside one's scope. | A read outside the scope answers not found; record policies decide each read of a business record. | TF-035 |
| Denial of service | Locking the tenant out of administration. | The last active tenant administrator cannot be removed or deactivated. | TF-032 |
| Elevation of privilege | An endpoint without a check. | Every endpoint declares its permission, signed-in or anonymous, and a host does not start otherwise; the anonymous list is fixed. | TF-030, TF-031 |
| Elevation of privilege | Access that outlives its need. | Access reviews; delegations end; a delegator who loses a role loses it for the delegate. | TF-038, TF-037 |

## What remains

- A delegator who loses a role loses it for the delegate within the grants cache's 60 seconds in other processes.
- A record-scoped assignment does not check that its record exists, and is not in an access review (later items).
- The seed's changes to system roles raised as privilege grants with alerts are T10b's (TF-079).
