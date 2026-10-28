# Threat model: the secret store and the keys

- **Modules:** keys
- **Endpoints:** none
- **Reviewed:** 2026-10-28 (S3 T10a)

## What it protects

Each tenant's data, index and signing keys in the key service (OpenBao's transit engine on the laptop, Azure Key Vault
later), the database passwords, each unit's secret, and the fields encrypted with those keys.

## Who can reach it

A unit, with a store token that may use its own tenant's keys and read its own secret only; the fleet, which makes,
rotates and retires keys; an operator holding the key officer permission; a tenant's administrators, who read the
Keys page.

## Threats

| Category | Threat | Controls | Requirements |
|---|---|---|---|
| Spoofing | A unit using another tenant's key. | The store's policy per tenant; a test proves another tenant's key refused. | TF-074, TF-075 |
| Tampering | Swapping ciphertext between records or versions. | Authenticated encryption; every value carries its key's version; a retired version no longer decrypts. | TF-074, TF-075 |
| Repudiation | A rotation nobody owns. | Every key operation is audited with the operator and the reason, in the control plane and in the tenant's trail. | TF-075 |
| Information disclosure | Keys or secrets leaving the store. | The application never holds a key; a database dump shows ciphertext only; exact search through a keyed hash; secrets masked in every log line; the repository's secret scan in CI. | TF-073, TF-074 |
| Denial of service | Rotation stopping the service. | Re-encryption in batches while the old version still decrypts; a signing key's old version validates its tokens for 30 minutes; a rotated database password read again without a restart. | TF-075, TF-073 |
| Elevation of privilege | Rotating or retiring keys without the right. | The key officer permission, critical, with a reason and a fresh token; a unit's token can never rotate, retire or export. | TF-075 |

## What remains

- Azure Key Vault behind the same interface, TLS inside the Azure network and encryption at rest with customer keys
  where a contract asks it: the first Azure deployment's requirements.
- A store that loses a tenant's keys loses its encrypted fields; the store's own backups come with Azure.
- The control plane's keys are not rotated yet (S6).
