# Risk: S3 T9b, keys and transport

The task touched these requirements (classes from `docs/trace/risk.md`):

| Requirement | Class | How it was touched |
|---|---|---|
| TF-073 Secrets management | critical (Security) | The database password read again without a restart; separate store tokens and policies for the control plane's API and the fleet; secrets masked in logs and exceptions; the repository scan pinned and tested |
| TF-074 Encryption at rest and in transit | critical (Security) | Encrypted and searchable fields; TLS 1.2 and up at the ingress |
| TF-075 Key management and rotation | critical (Security) | Keys per tenant in the key service, versioned, rotated on schedule and on demand by a key officer, values moved before the old version is retired |
| TF-013 Multi-factor authentication | critical (Identity) | Authenticator secrets moved from the retired factor key to the tenant's data key |
| TF-020 Sessions and tokens, TF-023 Service accounts | critical (Sessions and tokens) | Access tokens signed by the key service, with key identifiers and an overlap after a rotation |
| TF-072 Rate limiting and anti-automation | critical (Security) | The login's check signed with the tenant's signing key |
| TF-069 Operational logs separate from audit | high | The scrubbing rule extended to secrets and exceptions |
| TF-210 Provisioning | critical (Tenancy and control plane) | The secrets step makes the tenant's keys and grants only them |

The change is classed **critical**.

**What could go wrong.** A tenant's data could become unreadable: a rotation that retires a key version before every value has moved, or an update that loses the authenticator secrets sealed under the old factor key, would lock people out or lose fields for good. A tenant's unit could use another tenant's keys, and so read its fields or sign its tokens. Tokens signed before a rotation could fail early and sign everyone out, or a retired version could keep validating. A rotated database password could take a unit down until a restart. A secret could reach a log line through an exception's text. The control plane's API, if compromised, could still read the fleet's administrative database connection.

**What proves it does not.** The re-encryption tests move every value of every module context to the new version, resume an interrupted run, keep a row changed meanwhile, and only then see the old version retired; a value of a retired version is refused (Data `EncryptedFieldTests`, Keys `KeyRotationSyncTests`, Fleet `KeyRotationTests`, TF-075 AC1, AC3). A unit's token is refused another tenant's key and the right to rotate its own (Security `KeyServiceTests`, Fleet `ProvisioningTests`, TF-074 AC5). A token signed before a rotation validates until it expires, the new version signs new ones, and the retired version leaves the published keys (Identity `TokenTests`, TF-075 AC2). A password changed in the database and the store is used by open and new connections without a failed request (Data `PasswordRotationTests`, Security `StoreDatabaseCredentialsTests`, TF-073 AC2). Exceptions holding a connection string, a token or a key are logged and traced scrubbed (Telemetry tests, TF-073 AC3). The API's token cannot read the fleet's secret, the migrate secret or the tokens, nor make a token, and the fleet's cannot read the tokens (Fleet `KeyRotationTests`, ADR-0054). A rotation without `tenancy.keys.manage`, a reason or a fresh token is refused, and every step is audited with who asked and why (Tenancy `KeyEndpointTests`, TF-075 AC3, AC4). On the laptop, acme, bravo and echo were updated: echo's 17 authenticator secrets moved to its data key and its old column emptied, no unit or fleet secret holds a signing or factor key any more, and the smoke's `tls` and `key-rotation` steps prove the ingress's policy and an index key's rotation end to end (TF-074 AC3, TF-075 AC1).

**Results.** make verify passed: 1171 .NET test cases, 389 Vitest, 148 scans, 63 Python; TF-073, TF-074 and TF-075 have a test for every criterion (3 of 3, 5 of 5, 5 of 5), and 42 tests name ADR-0054. A full scan with the pinned gitleaks, every commit and the tree, finds nothing. On the laptop the smoke passed 23 of 23 on echo, also with echo on Standard as the release's tenant is, and the browser tests 40 of 40.

**What is left.** The control plane's own keys and database password are made and used as a tenant's, but not yet rotated (S6, with the operator console). Azure Key Vault behind the same interface, and encryption at rest with customer-managed keys, are the first Azure deployment's (ADR-0054, section 6). The old column of factor secrets stays until a later migration drops it.
