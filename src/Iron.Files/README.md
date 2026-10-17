# Iron.Files

Object storage abstraction, virus scan, type and size rules, signed links, retention, document rendering, signature capture. Schema: `files`.

Slice S1 (T12): the storage interface with the MinIO (S3) implementation and the readiness probe. Upload, download, versions and signed links follow in S4.

Folders: `Api/` endpoints (thin), `Services/` business logic (plain classes), `Data/` entities, repositories and migrations for this module's schema, `Contracts/` the public interfaces and DTOs other modules may use (its own project, `Iron.Files.Contracts`).

Only `Contracts/` is public. Another module may reference `Iron.Files.Contracts` and nothing else from this folder; the build checks enforce it.
