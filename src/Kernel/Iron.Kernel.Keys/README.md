# Iron.Kernel.Keys

Kernel module (ADR-0029). Schema `kernel_keys`. A unit's side of key rotation (TF-074, TF-075, ADR-0054). The keys themselves are the key service's (`IKeyService` in the kernel vocabulary, OpenBao's transit engine on the laptop); rotating them is the operator's, through the control plane, and the fleet carries it out. This module is what happens in the unit.

| Folder | Holds |
|---|---|
| `Contracts/` | `KeysPermissions` (`keys.keys.read`, `keys.checks.manage`), `KeysAuditEvents` (`keys.key_rotated`, `keys.reencryption_finished`, `keys.key_retired`, `keys.check_stored`), `KeysSettings` (`keys.rotation.data_days`, the tenant's policy for its data and index keys: 30 to 730 days, 365 by default, kept within the operator's bounds by the control plane), `KeysErrors` |
| `Data/` | `KeysDbContext`: `KeyState` (each key as the control plane last answered: versions, last and next rotation, the days between), `KeyRotationEntry` (each rotation as the unit recorded it, its id the control plane's, with the re-encryption's progress here), `KeyCheck` (a value encrypted with the data key and found by its exact value through its keyed hash) |
| `Services/` | `KeyRotationSync`, one pass of the worker: ask the control plane (as the unit's own client, `ControlPlaneClient`) for the tenant's keys and rotations; record a new rotation in the tenant's audit trail with who asked and why, and a retirement; tell the control plane the tenant's policy when it differs; and for a rotated data or index key run Data's `FieldReencryption`, which moves every encrypted value and search hash of every module to the new version, reporting the progress at most once a second and then that it finished, so the fleet retires the old version. `KeyWorker` runs a pass every `Keys:PollSeconds` (15) in the worker host (`AddKeyRotation()`). `KeyChecks` stores and finds key checks |
| `Api/` | `GET /api/v1/keys` (the Keys page: each key's version, minimum version, last and next rotation and days between, the running re-encryption's progress, the latest 20 rotations), `POST /api/v1/keys/checks` and `POST /api/v1/keys/checks/search` (the value in the body, never in a path) |

A signing key's rotation needs nothing here beyond its record: the Identity module reads the signing key's live versions once a minute, and the fleet retires the old version only after every token it signed has expired. A database password's rotation is recorded too; the data source reads the new password from the store (ADR-0054).

Without `ControlPlane:*` in the unit secret, as on a host run from source, the worker asks nothing and the Keys page shows no keys.

Tests: `tests/Kernel/Iron.Kernel.Keys.Tests` (PostgreSQL and OpenBao's transit engine, the control plane standing in).

Model change: edit the entity, then `make ef-add MODULE=Keys NAME=<WhatChanged>` and commit `Data/Migrations`.
