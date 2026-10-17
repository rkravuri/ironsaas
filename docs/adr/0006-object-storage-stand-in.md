# ADR-0006: Azurite replaces MinIO as the laptop object storage

**Status:** Proposed (confirm before T12)
**Date:** 2026-10-18
**Decided by:** Product owner and architect

## Context

PRD section 6 chose MinIO (S3 API, AGPL) for the laptop and Azure Blob Storage for Azure, both behind the `Iron.Files` interface. That choice no longer holds: MinIO stopped publishing community binaries and container images in October 2025 and archived its repository in 2026. Its last release is from September 2025 and security fixes no longer reach the community edition.

## Decision

- Azurite, Microsoft's open source Azure Storage emulator (MIT), is the laptop stand-in for Azure Blob Storage. Image `mcr.microsoft.com/azure-storage/azurite`, blob service only, one account with a generated key.
- `Iron.Files` gets one storage implementation in S1: the Azure Blob SDK. It runs against Azurite on the laptop and against Azure Blob Storage in Azure with a change of connection settings only. The `IObjectStorage` interface stays, so a second implementation (S3 or another API) can be added if a deployment outside Azure ever needs it.
- "A storage bucket with its own credentials" per tenant (TF-210) becomes a blob container per tenant with a container-scoped shared access signature issued by the fleet; in Azure the same, or a managed identity with role assignment on the container.
- File versions (TF-160) are kept by `Iron.Files` itself (version rows and a new blob name per version), not by storage-level versioning, so behaviour is identical on the laptop and in Azure.
- Integration tests use the Testcontainers Azurite module.

## Options considered

- SeaweedFS (Apache-2.0): mature, S3 API plus an IAM API for per-tenant keys. Would keep an S3 implementation alive next to the Blob one: two providers to write, test and keep equal. Second choice if an S3 need appears.
- RustFS (Apache-2.0): positions itself as a MinIO successor with a console; version 1.0 appeared in October 2026 and is too young to depend on.
- Garage (AGPL-3.0): solid, S3 API with an admin API, but needs a cluster layout step at first start and has no console.
- MinIO built from source: possible under the AGPL, but every security fix becomes our build, and the project is frozen.

## Consequences

- Easier: one storage provider, the same API on the laptop and in production, maintained by the cloud vendor we chose; lighter container; fewer moving parts.
- Harder: no web console in the container (Azure Storage Explorer or the VS Code extension browse it); a non-Azure deployment would need a second provider.
- Revisit: when the first non-Azure deployment is seriously discussed, or if Azurite falls behind the Blob API features Iron.Files needs.
