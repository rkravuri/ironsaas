# Backend rules (.NET)

The S1 modules are the templates: `src/Iron.Identity` for a module with entities, services and endpoints; `src/Iron.Fleet` for jobs and steps; `src/Iron.Tenancy` for a control plane module.

- A module has `Api/`, `Services/`, `Data/`, `Contracts/` (its own project) and a `README.md`. Another module's `Api`, `Services` and `Data` are private; expose through `Contracts` (ADR-0001).
- Entities derive from `AuditedEntity`, `AppendOnlyEntity` or `BusinessRecord`; snake_case tables in the module's schema; conventions in `Iron.Data` (ADR-0008). A migration is generated, read once, committed with the entity.
- Services return `Result`/`Result<T>`; error codes are `module.resource.reason`; exceptions are for programmer errors and infrastructure (ADR-0007).
- Endpoints are minimal APIs on `MapApiGroup`, with `RequirePermission`, `RequireSignedIn` or `AllowAnonymous`; lists take `ListQuery` and answer the envelope; problems carry `error_code` and `correlation_id` (ADR-0009, ADR-0012).
- A state change writes its audit event in the same unit of work (ADR-0013); event names are constants in the module's Contracts.
- Permissions are declared in Contracts and listed in the module's `Permissions`; seeds put them into the system roles that should carry them.
- Tests: unit tests for rules; integration tests on the fixtures that start PostgreSQL, Azurite and OpenBao (see `tests/Iron.Fleet.Tests/FleetFixture.cs`); endpoint tests through the in-memory host (`tests/Iron.Api.Tests`). Every behaviour the section names has a test.
- Configuration comes from the unit secret through `IConfiguration` (ADR-0016); never read the store directly in a module.
