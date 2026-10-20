// Iron.Host.Worker: see README.md in this folder. A worker serves no API; it runs the modules' background work.
using Iron.Kernel.Api;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Tenancy;
using Iron.Kernel.Audit;
using Iron.Kernel.Authorization;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Data.Health;
using Iron.Kernel.Entitlements;
using Iron.Kernel.Files;
using Iron.Kernel.Flags;
using Iron.Kernel.Identity;
using Iron.Kernel.Security;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Settings;
using Iron.Kernel.Telemetry;
using Iron.Kernel.Telemetry.Health;
using Iron.Kernel.Telemetry.Heartbeat;

if (HealthProbe.IsCommand(args))
{
    return await HealthProbe.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(DatabaseCommands.StripCommand(args));

// With Secrets:Address, Secrets:Token and Secrets:Path in the environment, the unit's secret supplies the rest of the
// configuration (connection string, storage, signing key); without them, the environment does (laptop, tests).
builder.Configuration.AddIronSecrets();
var tenant = TenantBinding.FromConfiguration(builder.Configuration);

// The modules this worker runs, in order: the same list as the API host, without endpoints.
IReadOnlyList<IModule> modules = [new AuditModule(), new AuthorizationModule(), new IdentityModule(), new FilesModule(), new EntitlementsModule(), new FlagsModule(), new SettingsModule()];

builder.AddIronTelemetry("iron-worker", telemetry => telemetry.TenantId = tenant.Id.Value);
builder.Services.AddIronTenant(tenant);
builder.Services.AddIronApi();
builder.Services.AddIronSecurity(builder.Configuration);
builder.Services.AddIronSecretStore(builder.Configuration);
builder.Services.AddIronHealth();
builder.Services.AddIronHeartbeat(builder.Configuration);

var connectionString = builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("ConnectionStrings:Database is required. Set ConnectionStrings__Database for this unit.");
builder.Services.AddIronDatabase(connectionString, builder.Configuration["Database:AppRole"]);
builder.Services.AddIronDatabaseHealthCheck();
builder.Services.AddModules(builder.Configuration, modules);

// The daily passes that belong to one process per unit, not to every API replica.
builder.Services.AddAccessReviewEscalation();
builder.Services.AddConflictReport();

var app = builder.Build();

if (DatabaseCommands.IsCommand(args))
{
    return await DatabaseCommands.RunAsync(app.Services, args, app.Lifetime.ApplicationStopping);
}

app.UseIronTelemetry();
app.UseIronApi();
app.UseIronSecurity();
app.UseIronTenant();
app.MapIronHealth();

await app.RunAsync();
return 0;
