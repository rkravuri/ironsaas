// Iron.Host.Fleet: see README.md in this folder. A worker serves no API; it runs the modules' background work.
using Iron.Api;
using Iron.Api.Modules;
using Iron.Api.Tenancy;
using Iron.Audit;
using Iron.Authorization;
using Iron.Identity;
using Iron.Data;
using Iron.Data.Commands;
using Iron.Data.Health;
using Iron.Security;
using Iron.Security.Secrets;
using Iron.Telemetry;
using Iron.Telemetry.Health;
using Iron.Telemetry.Heartbeat;

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
IReadOnlyList<IModule> modules = [new AuditModule(), new AuthorizationModule(), new IdentityModule()];

builder.AddIronTelemetry("iron-fleet", telemetry => telemetry.TenantId = tenant.Id.Value);
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
