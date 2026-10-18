# Iron.Data

Database conventions for every module (TF-080, TF-220, ADR-0008). No schema of its own; it defines how every module's schema looks and how it is reached.

| Namespace | What is in it |
|---|---|
| `Iron.Data.Entities` | `Entity` (uuid v7 key), `AuditedEntity` (created, updated, version), `AppendOnlyEntity` (written once), `BusinessRecord` (owning organisation and sharing mode), `ISoftDeletable`, `ITenantWide`, `SharingMode` |
| `Iron.Data.Contexts` | `ModuleDbContext`, the base of every module's DbContext, and `[Schema("...")]` |
| `Iron.Data.Conventions` | `ModelConventions` (applied to every model) and `AuditStampInterceptor` (fills the audit columns on save) |
| `Iron.Data.Checks` | `SchemaCheck` (a live database against the conventions) and `ModelCheck` (an EF model against the entity rules) |
| `Iron.Data.Lists` | `ApplyListQuery`, `ApplyFilters`, `ApplySorts` and `ToPageAsync`: a parsed `ListQuery` applied to an `IQueryable` with the resource's `ListFields` (TF-101) |
| `Iron.Data.Transactions` | `SharedConnection` (one connection per scope) and `IUnitOfWork` (one transaction for every module context of the scope; ADR-0013) |
| `Iron.Data.Commands` | `DatabaseCommands`: the `migrate`, `seed`, `schema-check` and `grant` commands of a host image, with `MigrationRunner`, `SeedRunner`, `SchemaCheckRunner` and `GrantsRunner` (the app role's privileges, derived from the models) over every registered module context |
| `Iron.Data.Seeding` | `ISeeder`: reference data a module needs, run by the `seed` command in order |
| `Iron.Data.Health` | `AddIronDatabaseHealthCheck()`: the database in the readiness probe |
| `Iron.Data` | `AddIronDatabase(connectionString)` and `AddIronDbContext<TContext>()` for hosts |

How a module uses it:

```csharp
[Schema("identity")]
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : ModuleDbContext(options)
{
    public DbSet<Principal> Principals => Set<Principal>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Principal>().HasIndex(p => p.LoginId).IsUnique();
    }
}
```

The conventions then give every table: snake_case names, `id uuid` set in code, `created_at`, `created_by`, `updated_at`, `updated_by`, `version` (append-only tables: only the created pair), an index on `owning_organisation_id` for business records, a query filter that hides soft-deleted rows, and the module's own migrations history table inside its schema. Modules never declare a foreign key into another module's schema; they store the other record's identifier.

Who acts: `AddIronDatabase` registers the system principal as the default `ICurrentPrincipal`. A host that serves people registers the request principal (Identity, T10), which takes over; workers and provisioning keep the system principal.

Writing an entity: derive from `BusinessRecord` for business and master data, `AuditedEntity` for platform data that changes, `AppendOnlyEntity` for rows that never change. Give it a private parameterless constructor for Entity Framework and public constructors for your code. Never set the audit columns yourself; the interceptor does it from `TimeProvider` and `ICurrentPrincipal`.

Tests: `tests/Iron.Data.Tests` (needs Docker: the schema and interceptor tests run against a throw-away PostgreSQL container).

Migrations: a module's design-time factory calls `DesignTime.OptionsFor<TContext>()` so `dotnet ef migrations add` (run as `make ef-add MODULE=<Module> NAME=<Change>`) builds the model with the same provider options as the host. Migrations are generated, never written by hand (ADR-0012).
