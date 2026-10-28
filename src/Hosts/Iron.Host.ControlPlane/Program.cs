using Iron.Host.ControlPlane;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.OpenApi;
using Iron.Kernel.Api.Tenancy;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Identity;
using Iron.Kernel.Protection;
using Iron.Kernel.Security;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Telemetry;
using Iron.Kernel.Telemetry.Health;

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
builder.AddControlPlane(tenant);

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
