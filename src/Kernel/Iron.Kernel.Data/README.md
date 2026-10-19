# Iron.Kernel.Data

Database conventions for every module (TF-080, TF-220, ADR-0008). No schema of its own; it defines how every module's schema looks and how it is reached.

| Namespace | What is in it |
|---|---|
| `Iron.Kernel.Data.Entities` | `Entity` (uuid v7 key), `AuditedEntity` (created, updated, version), `AppendOnlyEntity` (written once), `BusinessRecord` (owning organisation and sharing mode), `ISoftDeletable`, `ITenantWide` (belongs to the whole tenant, TF-220), `IOwnAccessRule` (not a business record; its module applies its own access rule, so its lists page directly; `ITenantWide` is one), `SharingMode` |
| `Iron.Kernel.Data.Contexts` | `ModuleDbContext`, the base of every module's DbContext, and `[Schema("kernel_<module>")]` (the prefix is the layer's: `kernel_`, `business_` or `products_`, ADR-0029) |
| `Iron.Kernel.Data.Conventions` | `ModelConventions` (applied to every model), `AuditStampInterceptor` (fills the audit columns on save) and `OwningOrganisationInterceptor` (a business record added, or moved to another owner, must name an active organisation unit; it asks `IOrganisationUnitCheck`, which Authorization implements, and throws `OwningOrganisationException` otherwise) |
| `Iron.Kernel.Data.Checks` | `SchemaCheck` (a live database against the conventions, and every module schema against the layer prefixes) and `ModelCheck` (an EF model against the entity rules) |
| `Iron.Kernel.Data.Lists` | `ApplyListQuery`, `ApplyFilters`, `ApplySorts` and `ToPageAsync`: a parsed `ListQuery` applied to an `IQueryable` with the resource's `ListFields` (TF-101); `ToPageAsync` on a raw query needs an `IOwnAccessRule` entity. `WhereAccessible(RecordAccess)` and `RecordAccessExtensions.Predicate`: the records one permission reaches, in one `= any(...)` predicate (ADR-0030) |
| `Iron.Kernel.Data.Policies` | Record policies (TF-035, ADR-0032): `IRecordPolicy<T>` (what a subject may read, and a decision for one record and an action), `RecordRules` (`UnitVisibility`, `OwnerWhile`, `All`, `Any`), `AddRecordPolicy`, `IRecordPolicies` (`ApplyAsync` answers a `PolicyQuery<T>`, the only query of a business record that pages; `FindAsync` answers `data.record.not_found` or `data.record.forbidden`), `RecordActions`, `PolicyDecision`, `PolicySubject`, and `IRecordAccess` (what one permission reaches, implemented by Authorization) |
| `Iron.Kernel.Data.Transactions` | `SharedConnection` (one connection per scope) and `IUnitOfWork` (one transaction for every module context of the scope; ADR-0013) |
| `Iron.Kernel.Data.Commands` | `DatabaseCommands`: the `migrate`, `seed`, `schema-check` and `grant` commands of a host image, with `MigrationRunner`, `SeedRunner`, `SchemaCheckRunner` and `GrantsRunner` (the app role's privileges, derived from the models) over every registered module context |
| `Iron.Kernel.Data.Seeding` | `ISeeder`: reference data a module needs, run by the `seed` command in order |
| `Iron.Kernel.Data.Health` | `AddIronDatabaseHealthCheck()`: the database in the readiness probe |
| `Iron.Kernel.Data` | `AddIronDatabase(connectionString)` and `AddIronDbContext<TContext>()` for hosts |

How a module uses it:

```csharp
[Schema("kernel_identity")]
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

Writing an entity: derive from `BusinessRecord` for business and master data, `AuditedEntity` for platform data that changes, `AppendOnlyEntity` for rows that never change. A business record needs a record policy registered in the module's `AddServices` (the architecture tests fail without one), and is listed and opened only through `IRecordPolicies`; an entity a list pages directly is marked `IOwnAccessRule`, or `ITenantWide` when it belongs to the whole tenant, and a business record carries neither. Give it a private parameterless constructor for Entity Framework and public constructors for your code. Never set the audit columns yourself; the interceptor does it from `TimeProvider` and `ICurrentPrincipal`.

Tests: `tests/Kernel/Iron.Kernel.Data.Tests` (needs Docker: the schema, interceptor and policy tests run against a throw-away PostgreSQL container). Its sample module is the reference for a module's entities and policies: `Widget` and `Proposal` (a draft status, changed by its creator while a draft) with their policies in `Sample/SamplePolicies.cs`.

Migrations: a module's design-time factory calls `DesignTime.OptionsFor<TContext>()` so `dotnet ef migrations add` (run as `make ef-add MODULE=<Module> NAME=<Change>`) builds the model with the same provider options as the host. Migrations are generated, never written by hand (ADR-0012).
