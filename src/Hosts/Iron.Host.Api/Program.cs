// Iron.Host.Api: see README.md in this folder.
using Iron.Host.Api;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.OpenApi;
using Iron.Kernel.Api.Tenancy;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Data.Health;
using Iron.Kernel.Identity;
using Iron.Kernel.Protection;
using Iron.Kernel.Security;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Telemetry;
using Iron.Kernel.Telemetry.Clock;
using Iron.Kernel.Telemetry.Health;

// The image answers "health" without building the host: container runtimes use it as the health check.
if (HealthProbe.IsCommand(args))
{
    return await HealthProbe.RunAsync(args);
}

// "catalogue <file>" writes what this build can license, which the control plane carries (make catalogue, ADR-0034).
if (UnitModules.IsCatalogueCommand(args))
{
    return await UnitModules.WriteCatalogueAsync(args);
}

// "version" and "configuration" say what release this image is and what it configures, for the fleet's update and the
// release's installation record (ADR-0052); like the catalogue, they build no host.
if (UnitModules.IsReleaseCommand(args))
{
    return await UnitModules.WriteReleaseAsync(args);
}

var builder = WebApplication.CreateBuilder(DatabaseCommands.StripCommand(args));

// With Secrets:Address, Secrets:Token and Secrets:Path in the environment, the unit's secret supplies the rest of the
// configuration (connection string, storage, signing key); without them, the environment does (laptop, tests).
builder.Configuration.AddIronSecrets();
var tenant = TenantBinding.FromConfiguration(builder.Configuration);

// The modules this host runs (UnitModules): the Foundation's, Audit first, then Authorization, Identity, Files and
// Entitlements; the modules of the suites the unit's configuration names (TF-244), and nothing of the others; and the
// sample module where the unit's configuration switches it on (ADR-0057).
var suites = UnitModules.InstalledFor(builder.Configuration);
var modules = UnitModules.ForUnit(builder.Configuration);

builder.AddIronTelemetry("iron-api", telemetry => telemetry.TenantId = tenant.Id.Value);
builder.Services.AddIronTenant(tenant);
builder.Services.AddIronApi();
builder.Services.AddIronSecurity(builder.Configuration);
builder.Services.AddIronSecretStore(builder.Configuration);
builder.Services.AddIronHealth();
builder.Services.AddIronClockCheck(builder.Configuration);
builder.Services.AddIronOpenApi("Foundation tenant API");

// T12 reads the connection string from the unit's secrets path; until then it comes from configuration.
var connectionString = builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("ConnectionStrings:Database is required. Set ConnectionStrings__Database for this unit.");
builder.Services.AddIronDatabase(connectionString, builder.Configuration["Database:AppRole"], builder.Configuration["Database:AuditReaderRole"]);
builder.Services.AddIronDatabaseHealthCheck();
builder.Services.AddModules(builder.Configuration, modules, suites);

// The browsers report to the API: a summary of their reports once a minute, which an alert rule counts (ADR-0056).
builder.Services.AddCspReportSummary();

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
app.UseIronProtection();
app.MapIronHealth();
app.MapIronOpenApi();
app.MapModules();

await app.RunAsync();
return 0;
