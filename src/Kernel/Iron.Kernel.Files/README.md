# Iron.Kernel.Files

Kernel module (ADR-0029). Schema `kernel_files`. File storage (TF-160; thin in S1, ADR-0016). Bytes live in the tenant's blob container (Azure Blob Storage, Azurite on the laptop; ADR-0006); the row in `kernel_files.files` is the truth about them: name, type, size, SHA-256, random storage key, record link.

| Folder | Holds |
|---|---|
| `Contracts/` | `IObjectStorage` and `StoredObject` (the storage abstraction other modules may use), `FilesPermissions` (`files.files.read`, `files.files.upload`, `files.files.delete`), `FilesErrors`, `FilesAuditEvents` |
| `Data/` | `FilesDbContext`, `StoredFile` (soft delete), the design-time factory, generated `Migrations/` |
| `Services/` | `BlobObjectStorage`, `FileService` (upload hashes while streaming, download verifies and audits), `HashingStream`, `LimitedStream`, `ObjectStorageHealthCheck`, `FilesSeeder` (creates the container), `StorageOptions` |
| `Api/` | `POST /api/v1/files` (multipart field `file`), `GET /api/v1/files/{id}`, `GET /api/v1/files/{id}/content`, `DELETE /api/v1/files/{id}` |

Configuration (`Storage` section, from the unit's secret in a unit): `ConnectionString` and `Container`, or `ContainerSasUrl`; `MaxUploadMegabytes` (50). Readiness fails with "The blob container '<name>' does not exist." until the container is there; `seed` creates it.

Model change: edit the entity, then `make ef-add MODULE=Files NAME=<WhatChanged>` and commit `Data/Migrations`.
