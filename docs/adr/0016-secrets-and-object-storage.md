# ADR-0016: Secrets at start and object storage

Date: 2026-10-18
Status: Accepted

## Context

TF-073: every secret lives in the store, is fetched at start, and never sits in configuration files or logs. TF-160: files in object storage under tenant prefixes with random keys, a recorded hash checked on download, soft delete. TF-133: readiness covers the database, the store and the storage with messages that say what is wrong. ADR-0006 chose Azure Blob Storage with Azurite on the laptop.

## Decision

1. **The unit's secret is configuration.** A unit's environment holds `Secrets:Address`, `Secrets:Token` and `Secrets:Path` (plus the tenant binding and the telemetry endpoint). `AddIronSecrets()` runs right after the host builder is created, reads the secret `<path>/unit` from the store and adds its keys as a configuration source above the environment. The keys are configuration keys (`ConnectionStrings:Database`, `Database:AppRole`, `Storage:ConnectionString`, `Storage:Container`, `Tokens:SigningKeyPem`), so nothing else in the host changes. A configured store that cannot be read, or a missing unit secret, stops the host with a message that names the address or the path. Without `Secrets:*`, the environment is the configuration, which is how the laptop and the tests run.
2. **OpenBao over its HTTP API.** `OpenBaoSecretStore` speaks KV version 2 with two endpoints and one header; no client library is needed for that, and the API has been stable for years. Key Vault gets its own implementation of `ISecretStore`. The fleet writes a unit's secret and token (T14); a unit reads its own path only.
3. **Object storage is one container per tenant** behind `IObjectStorage` (Iron.Files.Contracts), implemented with the Azure Blob SDK: a connection string and container name, or a container URL with a shared access signature. Keys are dated and random (`2026/10/<uuid>`), never derived from names.
4. **Hashes on both sides of the wire.** Uploads stream through a hashing wrapper into storage; the row stores name, type, size, SHA-256, key and record link, and is committed with its audit event. Downloads stream through the same wrapper; at the end the hash is compared with the row, the download or the integrity failure is audited, and the response carries `X-Checksum-SHA256` so clients can verify too. A size limit (`Storage:MaxUploadMegabytes`) is enforced on the declared and the actual length. Deleting marks the row (TF-165 decides when the bytes go).
5. **Readiness.** The database, the secret store (when configured) and the object storage are readiness checks; the storage check answers "The blob container '<name>' does not exist." until the container is there. The seed creates the container, so the laptop needs no extra step; the fleet creates it when it creates a unit.
6. **Signing keys** keep the three sources of ADR-0015; in a unit the PEM comes from the secret.

## Consequences

- The done-when of T12 holds on the laptop: `make secret-dev` writes the dev unit's secret, and `make run-api-secrets` starts the API with only the store address, the unit token, the path and the tenant in its environment.
- Secrets are read once at start; rotation without restart (TF-073 AC2) arrives with the reload mechanism in S3, and the store's readiness check already tells the fleet when a unit lost its store.
- Record-level download checks (TF-160 AC2) wait for record policies (TF-035); S1 checks the file permissions. Type rules and scanning (TF-161, TF-078) wait for their tasks.
- A corrupted object cannot be un-sent once streamed; the integrity failure is audited and the checksum header lets the client refuse the bytes. Full re-verification jobs belong to TF-165.
