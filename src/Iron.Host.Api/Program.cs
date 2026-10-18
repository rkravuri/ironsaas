// Iron.Host.Api: see README.md in this folder.
using Iron.Api;
using Iron.Api.Modules;
using Iron.Api.OpenApi;
using Iron.Api.Tenancy;
using Iron.Audit;
using Iron.Authorization;
using Iron.Data;
using Iron.Data.Commands;
using Iron.Data.Health;
using Iron.Security;
using Iron.Telemetry;
using Iron.Telemetry.Health;

// The image answers "health" without building the host: container runtimes use it as the health check.
if (HealthProbe.IsCommand(args))
{
    return await HealthProbe.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(DatabaseCommands.StripCommand(args));
var tenant = TenantBinding.FromConfiguration(builder.Configuration);

// The modules this host runs, in order: Audit first, because Authorization writes denial events through its contract.
// Identity (T10) and Files (T12) join the list with their tasks.
IReadOnlyList<IModule> modules = [new AuditModule(), new AuthorizationModule()];

builder.AddIronTelemetry("iron-api", telemetry => telemetry.TenantId = tenant.Id.Value);
builder.Services.AddIronTenant(tenant);
builder.Services.AddIronApi();
builder.Services.AddIronSecurity(builder.Configuration);
builder.Services.AddIronHealth();
builder.Services.AddIronOpenApi("Foundation tenant API");

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
app.MapIronHealth();
app.MapIronOpenApi();
app.MapModules();

await app.RunAsync();
return 0;
