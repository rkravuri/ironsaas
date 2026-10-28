# Threat model: signatures, evidence and held changes

- **Modules:** compliance, changes
- **Endpoints:** none
- **Reviewed:** 2026-10-28 (S3 T10a)

## What it protects

Electronic signatures and their manifestation, audit-trail reviews, evidence packs for auditors, the validation pack a
tenant downloads for its version, and configuration changes held for a second administrator.

## Who can reach it

Signed-in people with the compliance permissions; approvers of held changes, who need the change's permission at its
place; the requester of a change, who may withdraw it.

## Threats

| Category | Threat | Controls | Requirements |
|---|---|---|---|
| Spoofing | Signing as someone else. | Each signature proves the signer again (identifier, password and second factor, or a fresh company sign-in); nobody signs while impersonating, acting for someone, with a token or as a service account. | TF-063 |
| Tampering | Changing a record after it was signed. | A signature binds the SHA-256 of a canonical snapshot of the version it signs; a changed record shows against the signed one. | TF-063, TF-219 |
| Tampering | Changing a pack after it was made. | Every pack carries a manifest of its files' hashes and, for evidence, the chain's anchored head. | TF-067, TF-218 |
| Repudiation | Denying a signature or an approval. | The signature keeps who, when, what and its meaning; an approved change names both people. | TF-063, TF-068 |
| Information disclosure | An evidence pack in the wrong hands. | A high-risk permission; the download is a one-use link for the requester, five minutes, audited. | TF-067 |
| Denial of service | Packs made on demand without end. | Made by the worker in turn; exports limited to 20 an hour per person. | TF-072 |
| Elevation of privilege | One administrator changing a governed setting alone. | A held change waits for another person with the permission; the requester, impersonators, people acting for someone, service accounts and break-glass accounts are refused; a stale change is compared before it applies. | TF-068 |

## What remains

- The packs' PDF form waits for S4's document templates.
- Four-eyes becomes TF-247's field-level change requests in S5.
