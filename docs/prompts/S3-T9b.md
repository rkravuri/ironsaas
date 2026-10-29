## T9b. Keys, encryption and secrets

Sharpened on 2026-10-23 after T8. The second half of T9: encryption in transit and of chosen fields with tenant-specific keys (TF-074), versioned keys with scheduled and on-demand rotation (TF-075), and secrets that rotate without a restart and never leak (TF-073). It also takes T3's later item (the key that protects authenticator secrets must rotate too) and two of T9a's residuals (separate secrets for the control plane's API and the fleet, and the rotation of the sign-in check's key).

Three decisions shape it:

- **Keys are managed by a key service, not by the application.** On the laptop, OpenBao's transit engine holds versioned keys per tenant, encrypts and decrypts, rotates, and re-wraps values to a new version without exposing the key. In Azure, Key Vault takes the same role behind the same interface. The application never holds a data key in plain text longer than one operation.
- **Exact search through a keyed hash.** An encrypted field that must be found by its exact value (a tax identifier, a bank account) also stores an HMAC of its normalised value, with a tenant key of its own. A search hashes the input and compares hashes. The hash reveals nothing without the key, and the key rotates like the others.
- **Rotation never stops the service.** A rotated data key starts a re-encryption job; the old version keeps decrypting until the job finishes, then it is retired. A rotated signing key is published beside the old one, so tokens signed before the rotation stay valid until they expire.

### Opening message

```
Read CLAUDE.md, .claude/rules/backend.md, .claude/rules/frontend.md, docs/STATUS.md,
docs/adr/0016, 0028, 0046, 0047, 0050 and the ADRs of T8 and T9a, and TF-073, TF-074 and TF-075
in docs/prd/foundation/technical-foundation-PRD.md. Then read src/Kernel/Iron.Kernel.Security (the
secret store, its client), src/Kernel/Iron.Kernel.Data (the connection setup, the unit of work, the
model check), src/Kernel/Iron.Kernel.Identity (token signing, the authenticator secrets and their
key), src/Kernel/Iron.Kernel.Telemetry (the scrubbing rule), src/Kernel/Iron.Kernel.Fleet and
src/Kernel/Iron.Kernel.Tenancy (provisioning, updates, the secrets step), infra/compose (OpenBao,
Traefik's TLS configuration), the data tests' sample module, and the control plane's operator
commands. Then read docs/prompts/S3.md: replace section T9b with docs/prompts/S3-T9b.md, delete
that file, and commit the prompt alone first on a new branch slice/s3-t9b from main. Then follow
section T9b: write a short plan into the session, and build without waiting for approval unless the
"When to stop" rule in CLAUDE.md applies. Done when: the ingress refuses TLS below 1.2 and offers
only modern ciphers; fields marked encrypted are unreadable in a database dump and found by exact
value through a keyed hash; keys are versioned per tenant, rotate on a schedule and on demand, and a
re-encryption job moves every value to the new version before the old one is retired; a rotated
signing key leaves older tokens valid until they expire; a rotated database password is used
without a restart; the control plane's API and the fleet have separate secrets; secrets are masked in every log line, including exceptions; key operations need
the key officer permission, a reason and step-up, and are audited; every new test names what it
proves; make verify passes, and make smoke TENANT=echo and make e2e TENANT=echo pass. Finish with
the report, its Risk section, the matrix's counts for each requirement, the brief's six questions for
the pages you changed, and screenshots of them in both schemes. Then stop.
```

### Instructions

1. **TLS** (TF-074 AC3). Traefik accepts TLS 1.2 and 1.3 only, with modern cipher suites. A test connects with a TLS 1.0 client and is refused. Inside the laptop's platform network, traffic stays plain HTTP; record in the ADR that the Azure deployment uses TLS everywhere, including inside the network, and encrypts databases, backups and storage at rest. That is the first Azure deployment's requirement.
2. **The key service.** A kernel interface for encrypting, decrypting, signing, hashing and rotating with named, versioned keys. On the laptop it uses OpenBao's transit engine, with keys per tenant (for example `tenant-<slug>-data` and `tenant-<slug>-index`) created by the provisioning's secrets step and by `make update-unit` for existing units. A unit's store token may use only its own tenant's keys. Record in the ADR how Azure Key Vault takes the same place.
3. **Encrypted fields** (TF-074 AC1, AC2).
   - A property marked encrypted is stored as ciphertext with its key version, through the key service. A database dump shows nothing readable (AC1).
   - A property marked searchable also stores a keyed hash (HMAC) of its normalised value, with the tenant's index key. A search by exact value hashes the input and compares (AC2). No other kind of search works on such a field, and the developer guide says so.
   - Prove it on the data tests' sample module (a tax identifier and a bank account), and move T3's authenticator secrets onto it, replacing `Identity:FactorKey` (T3's later item).
4. **Versions and rotation** (TF-075).
   - Data keys rotate on a schedule (a tenant policy within the operator's bounds, default yearly) and on demand. A rotation starts a re-encryption job that re-wraps every value to the new version, in batches, with its progress visible. When it finishes, the old version is retired, so it can no longer decrypt (AC1).
   - The index key rotates the same way; the job recomputes the hashes.
   - The sign-in check's signing key (T9a) rotates the same way, with the old key still checking answers issued before the rotation until they expire.
   - Token signing keys rotate yearly and on demand. The new key signs new tokens, and the old key stays published until every token it signed has expired (AC2).
   - Every key operation is audited with the operator and the reason, in the control plane and in the tenant's trail (AC3). It needs the key officer permission `tenancy.keys.manage` (critical), a reason and step-up; without the permission it is refused (AC4).
   - Make targets: `make rotate-key TENANT=x KEY=data|index|signing REASON="..."` and `make key-status TENANT=x`.
5. **Secrets** (TF-073).
   - A rotated database password is picked up without a restart: the connection setup reads it from the store on a short schedule and on an authentication failure, and new connections use it (AC2). The fleet rotates database passwords on a schedule and on demand, with the same audit as keys.
   - **Separate secrets for the control plane's API and the fleet** (T9a's residual). Each gets its own secret path and its own store token with only the grants it needs. The API never holds the fleet's administrative database connection, and a test proves the API's token cannot read the fleet's secret.
   - The scrubbing rule masks connection strings, passwords, tokens and keys in every log line, including the text of exceptions (AC3).
   - The repository scan (gitleaks) stays in CI and fails on a finding (AC1); a test with a fixture proves the build fails and names the file and the secret type.
6. **The shell.** A Keys page in the console for the tenant's administrators: each key with its version, last rotation, next scheduled rotation and the running re-encryption job's progress, read-only (rotation is the operator's). Every string in the catalogue; the scan covers it in both schemes.
7. **Tests.** Each names its criteria, and every criterion of the three requirements is covered. Also: a dump read through the database role shows ciphertext only; a search by exact value found through the hash and nothing found by a partial value; a re-encryption job interrupted and resumed; a retired version refusing to decrypt; a token signed by the old key accepted until it expires; a rotated database password used by new connections while requests keep succeeding; a unit unable to use another tenant's key. The smoke gains a step that rotates the tenant's index key and still finds its sample value by exact match.
8. **On the laptop.** `make images`, `make up-control`, `make update-unit` for acme, bravo and echo (which creates their keys and moves the authenticator secrets), `make smoke TENANT=echo`, `make e2e TENANT=echo`.

Documentation: the next free ADR (the key service and its two back ends; per-tenant keys; keyed hashes for exact search and their limits; rotation, re-encryption and retirement; signing key overlap; the key officer; TLS and encryption at rest in Azure); the Security, Data and Identity READMEs; the developer guide (marking a field encrypted or searchable); the runbook (rotating a key, reading a re-encryption job, rotating a database password); STATUS.

### Review checklist

- [ ] No data key in plain text in the application beyond one operation.
- [ ] A dump shows ciphertext; exact search works through the hash and nothing else does.
- [ ] Rotation moves every value to the new version before the old one is retired.
- [ ] Old tokens stay valid until they expire after a signing key rotation.
- [ ] A database password rotates with no restart and no failed request.
- [ ] Every key operation is audited with the operator and the reason.
- [ ] The control plane's API cannot read the fleet's secret.
