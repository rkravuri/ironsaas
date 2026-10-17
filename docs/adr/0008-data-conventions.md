# ADR-0008: Data conventions

**Status:** Accepted
**Date:** 2026-10-18
**Decided by:** Product owner and architect

## Context

TF-080 fixes the shape of every table: one schema per module, snake_case names, a surrogate key, created and updated columns, a concurrency version, soft-delete flags where deletion is allowed, and no foreign key from one module's schema into another. TF-220 adds the owning organisation and sharing mode on every business record and asks the build to fail when one is missing. P8 says audit and history rows are never updated or deleted. These rules must come for free with the base classes, and the build must check them.

## Decision

- Entity Framework Core with the Npgsql provider is the data access layer (PRD section 6). Each module has one `DbContext` deriving from `ModuleDbContext`, declares its schema with `[Schema("name")]`, maps its entities in `ConfigureModule`, and the Foundation conventions run afterwards. The context's migrations live in the module's `Data` folder and its migrations history table is `__ef_migrations_history` inside the module's schema, so modules migrate independently.
- Base classes carry the standard columns: `Entity` (uuid v7 key set in code, `ValueGeneratedNever`), `AuditedEntity` (created, updated, version), `AppendOnlyEntity` (created only; the table carries the comment `append-only`), `BusinessRecord` (owning organisation, sharing mode, an index on the organisation). `ISoftDeletable` marks rows that are never physically deleted; a global query filter hides marked rows.
- The audit columns are written by a `SaveChangesInterceptor` from the injected `TimeProvider` and `ICurrentPrincipal`. Code never sets them. The same interceptor turns a delete of a soft-deletable row into a mark and refuses any change to an append-only row.
- `version` is an explicit integer column and the concurrency token, incremented on every save. It is visible to the API for ETags (S3) and portable across databases; PostgreSQL's `xmin` is not used.
- Names are snake_case through EFCore.NamingConventions, configured once in `AddIronDbContext`.
- Cross-schema foreign keys are forbidden. A module stores the other module's record identifier as a plain `uuid`.
- Two checks enforce the rules. `SchemaCheck` reads a live database (information_schema and pg_catalog) and reports every table that misses a standard column, has the wrong type or nullability, is not snake_case, has a primary key other than `id`, or a foreign key into another schema. `ModelCheck` reads an EF model and reports entities that do not derive from `Entity`, and in a product every entity that is neither a `BusinessRecord` nor marked `ITenantWide`. The architecture tests run `ModelCheck` over every context in the repository; the pipeline runs `SchemaCheck` against every migrated database from T7.
- Integration tests run against a throw-away PostgreSQL 19 container through Testcontainers; the sample module in `tests/Iron.Data.Tests` is the reference for how a module writes its context and entities.
- `ICurrentPrincipal` lives in Iron.Core; hosts register the request principal (T10) or `SystemPrincipal` in workers. The system principal has one fixed identifier in every tenant.

## Options considered

- `xmin` as the concurrency token. Rejected: invisible to clients and PostgreSQL only.
- Database defaults and triggers for the audit columns. Rejected: the values must come from the application's clock and principal so that tests can control them and so that the actor is the real principal, not a database role.
- Separate DbContext per host with all modules' entities. Rejected: one context per module keeps ownership of a schema with one owner and lets modules migrate on their own.
- Hard deletes with an archive table. Rejected for business records: P8 and 21 CFR Part 11 need the row and its history in place.

## Consequences

- Easier: a module writes entities and a context and gets every TF-080 rule without thinking about it; the two checks catch mistakes before a reviewer does.
- Harder: every entity needs a private parameterless constructor for Entity Framework next to its real constructors; the README shows the pattern. Tests of the data layer need Docker.
- Revisit: partitioning rules for the largest tables (TF-226, S9) will add constraints the schema check must learn (no unique constraint without the partition key).
