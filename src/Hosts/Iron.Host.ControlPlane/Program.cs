// Iron.Host.ControlPlane: see README.md in this folder.
using Iron.Kernel.Api;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.OpenApi;
using Iron.Kernel.Api.Tenancy;
using Iron.Kernel.Audit;
using Iron.Kernel.Authorization;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Data.Health;
using Iron.Kernel.Fleet;
using Iron.Kernel.Identity;
using Iron.Kernel.Security;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Telemetry;
using Iron.Kernel.Telemetry.Health;
using Iron.Kernel.Tenancy;

// The image answers "health" without building the host: container runtimes use it as the health check.
if (HealthProbe.IsCommand(args))
{
    return await HealthProbe.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(DatabaseCommands.StripCommand(args));

// With Secrets:Address, Secrets:Token and Secrets:Path in the environment, the unit's secret supplies the rest of the
// configuration (connection string, storage, signing key); without them, the environment does (laptop, tests).
builder.Configuration.AddIronSecrets();
var tenant = TenantBinding.FromConfiguration(builder.Configuration);

// The modules this host runs, in order: Audit first (the others write events through its contract), then
// Authorization, then Identity (it replaces the default principal), then Fleet and Tenancy (Tenancy asks Fleet for jobs). The control plane stores no tenant files.
IReadOnlyList<IModule> modules = [new AuditModule(), new AuthorizationModule(), new IdentityModule(), new FleetModule(), new TenancyModule()];

builder.AddIronTelemetry("iron-control-plane", telemetry => telemetry.TenantId = tenant.Id.Value);
builder.Services.AddIronTenant(tenant);
builder.Services.AddIronApi();
builder.Services.AddIronSecurity(builder.Configuration);
builder.Services.AddIronSecretStore(builder.Configuration);
builder.Services.AddIronHealth();
builder.Services.AddIronOpenApi("Foundation control plane API");

// T12 reads the connection string from the unit's secrets path; until then it comes from configuration.
var connectionString = builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("ConnectionStrings:Database is required. Set ConnectionStrings__Database for this unit.");
builder.Services.AddIronDatabase(connectionString, builder.Configuration["Database:AppRole"]);
builder.Services.AddIronDatabaseHealthCheck();
builder.Services.AddModules(builder.Configuration, modules);

var app = builder.Build();

// migrate, seed and schema-check run against the unit's database and exit.
if (DatabaseCommands.IsCommand(args))
{
    return await DatabaseCommands.RunAsync(app.Services, args, app.Lifetime.ApplicationStopping);
}

app.UseIronTelemetry();
app.UseIronApi();
app.UseIronSecurity();
app.UseIronTenant();
app.UseIronIdentity();
app.MapIronHealth();
app.MapIronOpenApi();
app.MapModules();

await app.RunAsync();
return 0;
