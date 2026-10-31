// Iron.Host.Worker: see README.md in this folder. A worker serves no API; it runs the modules' background work.
using Iron.Kernel.Api;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Tenancy;
using Iron.Kernel.Audit;
using Iron.Kernel.Authorization;
using Iron.Kernel.Changes;
using Iron.Kernel.Compliance;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Data.Health;
using Iron.Kernel.Entitlements;
using Iron.Kernel.Events;
using Iron.Kernel.Files;
using Iron.Kernel.Flags;
using Iron.Kernel.Identity;
using Iron.Kernel.Jobs;
using Iron.Kernel.Keys;
using Iron.Kernel.Monitoring;
using Iron.Kernel.Monitoring.Contracts;
using Iron.Kernel.Protection;
using Iron.Kernel.Samples;
using Iron.Kernel.Security;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Settings;
using Iron.Kernel.Telemetry;
using Iron.Kernel.Telemetry.Clock;
using Iron.Kernel.Telemetry.Health;
using Iron.Kernel.Telemetry.Heartbeat;
using Iron.Kernel.Visibility;
using Iron.Suites.Procurement.Purchasing;

if (HealthProbe.IsCommand(args))
{
    return await HealthProbe.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(DatabaseCommands.StripCommand(args));

// With Secrets:Address, Secrets:Token and Secrets:Path in the environment, the unit's secret supplies the rest of the
// configuration (connection string, storage, signing key); without them, the environment does (laptop, tests).
builder.Configuration.AddIronSecrets();
var tenant = TenantBinding.FromConfiguration(builder.Configuration);

// The modules this worker runs, in order: the same lists as the API host, without endpoints: the Foundation's, then
// those of the suites the unit's configuration names (TF-244, ADR-0059), and the sample module where the unit is marked
// for testing (ADR-0057), so the subscriptions of each are handled here, where the dispatcher runs (ADR-0060).
// Protection is here for its audit event types, which the alert rules count (ADR-0056); its limits apply to requests,
// which the worker serves none of.
IReadOnlyList<IModule> foundation = [new AuditModule(), new EventsModule(), new JobsModule(), new AuthorizationModule(), new IdentityModule(), new FilesModule(), new EntitlementsModule(), new FlagsModule(), new SettingsModule(), new VisibilityModule(), new ComplianceModule(), new ChangesModule(), new ProtectionModule(), new KeysModule(), new MonitoringModule()];
var suites = InstalledSuites.From(builder.Configuration, [PurchasingModule.Suite()]);
var composed = InstalledSuites.Compose(foundation, suites);
IReadOnlyList<IModule> modules = SamplesModule.IsEnabled(builder.Configuration) ? [.. composed, new SamplesModule()] : composed;

builder.AddIronTelemetry("iron-worker", telemetry => telemetry.TenantId = tenant.Id.Value);
builder.Services.AddIronTenant(tenant);
builder.Services.AddIronApi();
builder.Services.AddIronSecurity(builder.Configuration);
builder.Services.AddIronSecretStore(builder.Configuration);
builder.Services.AddIronHealth();
builder.Services.AddIronClockCheck(builder.Configuration);
builder.Services.AddIronHeartbeat(builder.Configuration);

var connectionString = builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("ConnectionStrings:Database is required. Set ConnectionStrings__Database for this unit.");
builder.Services.AddIronDatabase(connectionString, builder.Configuration["Database:AppRole"], builder.Configuration["Database:AuditReaderRole"]);
builder.Services.AddIronDatabaseHealthCheck();
builder.Services.AddModules(builder.Configuration, modules, suites);

// The daily passes that belong to one process per unit, not to every API replica.
builder.Services.AddAccessReviewEscalation();
builder.Services.AddConflictReport();

// Pending configuration changes nobody decided in time expire (TF-068, ADR-0052).
builder.Services.AddChangeExpiry();

// Evidence packs people asked for are made here, a few seconds after they ask (TF-067, ADR-0052).
builder.Services.AddEvidencePacks();

// The audit chain's sealing and anchors (TF-061, ADR-0050): one worker per unit seals, every few seconds.
builder.Services.AddAuditSealing(builder.Configuration);

// Key rotation's side in the unit (ADR-0054): rotations recorded in the tenant's trail, every encrypted value moved.
builder.Services.AddKeyRotation();

// The security alert rules every minute, and each alert copied to the control plane (TF-079, ADR-0056).
builder.Services.AddMonitoring(MonitoringScope.Unit);

// The domain events' dispatcher and their archive (TF-085, TF-111, ADR-0060): one worker per unit hands each event on.
builder.Services.AddEventDispatch();

// The jobs (TF-120, ADR-0061): this worker runs the unit's jobs, and any second worker shares them through the database.
builder.Services.AddJobRunner();

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
