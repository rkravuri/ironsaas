# ADR-0054: Keys, encryption and secrets: a key service with keys per tenant, encrypted and searchable fields, rotation without downtime, passwords read again, separate control plane secrets, and TLS 1.2 and up

**Status:** Accepted at the design session's review (2026-10-28), with the two password fixtures in Identity's tests made run-time values before the merge
**Date:** 2026-10-28
**Decided by:** Product owner and architect, in the brief of S3 T9b; the details here are the task's

## Context

S3 T9b brings TF-073 (secrets management), TF-074 (encryption at rest and in transit) and TF-075 (key management and rotation), and takes three items parked for it: the factor key of T3, which had one version and no rotation (ADR-0047); the sign-in check's own key, made at start (ADR-0053); and T9a's review, which moved two residuals here: the control plane's API and the fleet shared one secret and the store's root token, so the API held the fleet's administrative database connection, and the check's key rotated with nothing.

Before this task a unit held its keys itself: the token signing key in PEM and the factor key in its secret, read once at start. Its database password was read once too, so a new password needed a restart. Logs scrubbed links and personal data (ADR-0046, ADR-0050), not secrets, and not an exception's text.

The brief set three decisions: keys are managed by a key service, not by the application; an encrypted field found by exact value carries a keyed hash; and rotation never stops the service.

## Decision

### 1. The key service

- **One kernel interface, `IKeyService`** (`Iron.Kernel.Keys`): encrypt, decrypt, re-wrap, hash (HMAC-SHA256) and sign (ES256) with a tenant's named, versioned keys, and read their versions. Calls take lists, so a page of values is one round trip. Every value it makes carries the version that made it: `<service>:v<n>:<base64>`.
- **On the laptop, OpenBao's transit engine** (`TransitKeyService` in `Iron.Kernel.Security`, over its HTTP API as the KV store is, without a client library). The keys stay in the store, which does the work: the application never holds a key, not even for one operation.
- **In Azure, Key Vault takes the same place behind the same interface**: an RSA or AES key per purpose and tenant in a vault per region (managed HSM where the tenant's contract asks it), `encrypt`/`decrypt`/`wrapKey`, `sign`/`verify` and key versions; the unit's managed identity gets the Crypto User role on its own tenant's keys only; Key Vault's HMAC is done with a key-wrapped secret under the vault's key, as the first Azure deployment's implementation decides. The implementation is that deployment's; nothing above the interface changes.
- **The tests and a host run from source** use `LocalKeyService`: the same rules in the process, every key derived from one material by HKDF with the key's name and version (signing keys too, as a private scalar), so hosts that share a material agree. Never for a unit with a store.
- **`IKeyAdministration`**, the fleet's: make, describe, rotate and retire keys. A unit's token cannot.

### 2. Keys per tenant

- **Three keys per tenant**, made by the provisioning's secrets step and by `make update-unit` for existing units: `tenant-<slug>-data` (AES-256-GCM: encrypts chosen fields), `tenant-<slug>-index` (HMAC: the keyed hashes that find them) and `tenant-<slug>-signing` (ECDSA P-256: signs access tokens; its HMAC signs the login's check, so rotating it rotates both).
- **The unit's secret names its family** (`Keys:Prefix`), never a key. The unit's store token may encrypt, decrypt and re-wrap with its data key, hash with its index key, sign and hash with its signing key, and read their versions: nothing else, not another tenant's keys, and never rotate, retire or export. A test proves another tenant's key refused.
- **The control plane** has its own family (`control-data`, `-index`, `-signing`), made by `prepare-control`.

### 3. Encrypted and searchable fields (TF-074 AC1, AC2)

- **A property of type `EncryptedValue`** is stored as the key service's ciphertext with its version (a convention converts it), so a dump shows nothing readable. A module encrypts and opens through `IFieldEncryption`; a value is plain in memory only between opening it and showing it.
- **A property marked `[Searchable]`** keeps a `SearchHash` beside it, named after it with `Hash`: the keyed hash of its normalised value (compatibility-composed, without white space, hyphens, dots or slashes, in capitals) under the index key, indexed. A search hashes the input with every live version of the index key and compares, so a value is found during a rotation too. No other search works on such a field: not a part, not a prefix, not a range. The model check refuses a searchable field without its hash and a hash of nothing.
- **Field history** records an encrypted value only as changed and leaves its hash out (ADR-0051).
- **The first fields**: the sample module's supplier (a tax identifier, searchable, and a bank account), an authenticator app's secret (with the person's id sealed inside, so a copied secret does not open; the factor key of ADR-0047 is retired), a held enrolment secret, and the Keys module's key checks.

### 4. Rotation, re-encryption and retirement (TF-075)

- **Who rotates**: a key officer, holding `tenancy.keys.manage` (critical), with a reason and a fresh token (`POST /api/v1/tenants/{slug}/keys/{key}/rotations`, `make rotate-key`); without the permission it is refused (AC4). The schedule asks for the rotations due: the data and index keys by the tenant's policy (`keys.rotation.data_days`, 365 by default, kept within the operator's bounds, `Keys:Rotation:DataDaysMinimum` and `Maximum`, 30 and 730), the signing key yearly, the database password quarterly. One rotation of a key at a time.
- **The fleet carries it out** within seconds (`KeyRotationWorker`): it rotates in the key service and records the new version; the new version encrypts, hashes and signs from then on.
- **The unit moves every value** (the Keys module's worker asks the control plane every few seconds, as its own client): a data key's rotation re-wraps every `EncryptedValue` of every module inside the key service, a batch at a time; an index key's opens each searchable value, normalises it and hashes it again. A value already moved is not touched again, so an interrupted job resumes. The progress goes to the control plane, then that it finished.
- **Then the old version is retired** (`min_decryption_version`): nothing decrypts, hashes or verifies with it again (AC1). A signing key's old version is retired 30 minutes after the rotation (`SigningOverlapMinutes`): longer than an access token lives (15 minutes), plus the minute a unit may still sign with it before it reads the new one. Until then both are published in the JWKS and validate, so a token signed before the rotation stays valid until it expires (AC2). A login check issued before the rotation is checked with the version that signed it, for its five minutes.
- **Every step is audited with who asked and why** (AC3): in the control plane (`tenancy.key_rotation_requested`, `key_rotated`, `key_retired`, `key_rotation_failed`) and in the tenant's own trail (`keys.key_rotated`, `keys.reencryption_finished`, `keys.key_retired`), each naming the officer, or the schedule, and the reason.
- **The Keys page** (console, `keys.keys.read`) shows each key's versions, its last and next rotation and the running re-encryption's progress; it is read only.
- **Key checks**: a value the tenant stores encrypted and finds by its exact value (`keys.checks.manage`), which proves after a rotation that the keys still read and find; the smoke's key-rotation step uses one.

### 5. Secrets (TF-073)

- **A rotated database password is used without a restart** (AC2). With a store, the data source asks for the password at each new physical connection: the unit's secret is read again after a minute, or at once when PostgreSQL refused the password. The fleet writes the new password to the store first and sets it in the database next; a connection refused in between is retried, a little later each time, about three seconds in all, so no request fails, and open connections go on. Entity Framework's opens, the unit of work's and the readiness check's take the retry. The password applies only while the unit secret's login is the process's own, so the fleet's commands keep the owner's. The fleet rotates the app role's password on the schedule and on demand, audited as a key.
- **Separate secrets and tokens for the control plane's API and the fleet** (T9a's residual). `prepare-control`, run with the root token, writes `control/unit` (the API: its app connection, its anchors and its key family), `fleet/unit` (the fleet: the same, with the administrative database connection, the storage account's and the token role) and `control-migrate/unit` (the owner's), and gives each a policy and a token, kept where only the root token reads them (`control-tokens/unit`) and put into `.env` for Compose. The API's policy reads its own secret, writes the tenants' entitlements and flags and the platform's documents, takes a job's handover once and uses the control plane's keys: it cannot read the fleet's secret, the migrate secret or the tokens, nor make a token (a test proves it). The fleet makes the tenants' tokens through the token role `tenant-units`, which allows tenant policies only; it can write a tenant policy, so it administers the store for the tenants, which is what it is for.
- **Secrets are masked in every log line** (AC3): the scrubbing rule replaces a connection string's password, account key and shared access signature, a signature or token in a query string, a bearer token, a JSON Web Token, a private key in PEM, a store token, and the value of anything named a password, a secret, a key or a token. A logged exception whose text holds one goes out as a scrubbed stand-in, its original type named, to the console and to OpenTelemetry; a request's exception is recorded on its span as that stand-in, since a span's events cannot be changed once added.
- **The repository scan** (AC1): CI's gitleaks is pinned (8.30.1), and a test runs that version on a fixture made at run time: a private key in a file fails the scan naming the file and the rule; a clean folder passes. No commit holds a secret-shaped literal for the scan to find: tests build their sample secrets at run time.

### 6. Encryption in transit and at rest (TF-074 AC3)

- **The ingress accepts TLS 1.2 and 1.3 only**, and for TLS 1.2 only forward-secret AEAD suites (ECDHE with AES-GCM or ChaCha20-Poly1305) with X25519, P-256 and P-384 (`traefik/dynamic/tls-options.yaml`). The smoke writes ClientHellos byte by byte: TLS 1.0 is refused with a protocol version alert, TLS 1.2 with old suites with a handshake failure, TLS 1.2 with modern suites is answered.
- **Inside the laptop's platform network traffic stays plain HTTP**: the store, the database and the services talk on a private Docker network.
- **The Azure deployment uses TLS everywhere, inside the network too**: to Azure Database for PostgreSQL (`sslmode=verify-full`), to Key Vault and Storage over HTTPS only, between the ingress and the units (mutual TLS within the cluster), and to OpenBao where it runs. Databases, backups and object storage are encrypted at rest with platform-managed keys, and with customer-managed keys in Key Vault where a tenant's contract asks it. That is the first Azure deployment's requirement.

## Options considered

- **The application holding data keys, wrapped by the key service (envelope encryption with data keys cached)**: not chosen. It would hold a key for longer than one operation, which the brief refuses; the transit engine's batch calls make the round trips cheap enough.
- **An EF Core value converter that encrypts on save**: not chosen. A converter is synchronous, so it would call the key service synchronously for every value; explicit types and an asynchronous service keep the calls batched and visible.
- **Deterministic encryption for search**: not chosen. It reveals equal values everywhere and cannot rotate without changing every value; a keyed hash beside randomised ciphertext reveals only equality of the hash, and rotates.
- **The transit engine's own automatic rotation** (`auto_rotate_period`): not chosen. The rotation would happen without the operator, the reason or the audit, and the re-encryption would have to find it; the fleet's schedule asks for it like an officer does.
- **Rotating the database password with two alternating login roles**: not chosen. Every grant and trigger names the app role; reading the password again and retrying a refused open gives no failed request with one role.
- **The fleet keeping the root token**: not chosen at T9a's review; the fleet has a token of its own, and the root token is used only to prepare the control plane.
- **A committed fixture with a secret, allow-listed for CI**: not chosen. The fixture is made at run time, so the repository holds nothing the scan must be told to ignore.

## Consequences

- No unit or control plane process holds a key; a store that loses a tenant's keys loses its encrypted fields, as a lost factor key did before (ADR-0047). The store's backup is the first Azure deployment's, with Key Vault's soft delete and purge protection.
- An encrypted field can be found only by its exact value. A module that needs another search on a field does not encrypt it, or keeps a separate, less sensitive form; the developer guide says so.
- Every new physical database connection asks the store for the password at most once a minute; a password refused reads it at once.
- The control plane's own keys and database password are made and used as a tenant's, but their rotation is a later item, with S6's operator console: the control plane holds the operators' authenticator secrets and signs their tokens, and its schedule and re-encryption run in the fleet host.
- Units made before T9b get their keys at their next update, which also moves their authenticator secrets: the fleet takes the factor key out of the unit secret, keeps it in the fleet secret, which the unit cannot read, and hands it once to the seed. The old column holding those secrets stays until a later migration drops it.
- Identity's tests pick their common password from the module's list at run time. History keeps four findings, three of those passwords from T2 and a private key's opening line alone from S1, a placeholder: `.gitleaksignore` lists them by fingerprint, which names the commit, so the same text added in any other commit is still found. A full scan with the pinned gitleaks, history and tree, finds nothing.
- To revisit: the Azure deployment's Key Vault implementation and its HMAC; the control plane's key rotation; a rotation of a tenant with millions of encrypted values, whose job runs a batch at a time in one process today.
