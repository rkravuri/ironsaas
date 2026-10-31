using System.Net;
using System.Text.Json;
using Iron.Kernel;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit;
using Iron.Kernel.Authorization;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Entitlements;
using Iron.Kernel.Entitlements.Contracts;
using Iron.Kernel.Entitlements.Services;
using Iron.Kernel.Events;
using Iron.Kernel.Flags;
using Iron.Kernel.Telemetry;
using Iron.Suites.Procurement.Purchasing.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Suites.Procurement.Purchasing.Tests;

/// <summary>
/// The suite in a unit's API (TF-244, ADR-0059): the About page's endpoint with the installation, the plan and the
/// suite's module, the context that names the tenant's suites for the shell, and the suite's module switched off by its
/// own plan's dates and grace days, which are not the Foundation plan's. A unit without the suite has none of it.
/// </summary>
[Collection(PurchasingCollection.Name)]
public class AboutEndpointTests(PurchasingFixture fixture)
{
    private static readonly DateOnly Start = new(2026, 1, 1);

    private static readonly DateOnly Ends = new(2026, 6, 30);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-244 AC1", "TF-244 AC5", "ADR-0059")]
    public async Task TheAboutPageShowsTheInstallationAndPlanAndTheSuitesModuleFollowsItsOwnPlansGrace()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero));
        var job = Ids.New();
        await using var unit = await Unit.StartAsync(fixture, time, withSuite: true, job);
        var administrator = await unit.PersonWithAsync(SystemRoles.TenantAdministrator);
        var nobody = unit.Principals.Person("Nobody");
        await unit.PublishAsync(Document(Ends));

        using (var about = await unit.JsonAsync("/api/v1/suites/procurement/about", administrator))
        {
            var root = about.RootElement;
            Assert.Equal((ProcurementSuite.ShortName, ProcurementSuite.DisplayName, "2026.10.30.7"), (root.GetProperty("suite").GetString(), root.GetProperty("name").GetString(), root.GetProperty("version").GetString()));
            Assert.Equal(job, root.GetProperty("installation").GetProperty("installed_by_job").GetGuid());
            Assert.Equal((ProcurementSuite.EssentialsPlan, 30), (root.GetProperty("plan").GetString(), root.GetProperty("grace_days").GetInt32()));
            var module = Assert.Single(root.GetProperty("modules").EnumerateArray());
            Assert.Equal((PurchasingLicence.Code, "enabled", "licensed"), (module.GetProperty("code").GetString(), module.GetProperty("status").GetString(), module.GetProperty("reason").GetString()));
            Assert.Equal(("2026-06-30", "2026-07-30"), (module.GetProperty("ends_on").GetString(), module.GetProperty("grace_ends_on").GetString()));
        }

        using (var context = await unit.JsonAsync("/api/v1/me/context", administrator))
        {
            var entitlements = context.RootElement.GetProperty("entitlements");
            var suite = Assert.Single(entitlements.GetProperty("suites").EnumerateArray());
            Assert.Equal((ProcurementSuite.ShortName, ProcurementSuite.DisplayName, ProcurementSuite.EssentialsPlan), (suite.GetProperty("code").GetString(), suite.GetProperty("name").GetString(), suite.GetProperty("plan").GetString()));
            Assert.Contains(entitlements.GetProperty("modules").EnumerateArray(), m => m.GetProperty("code").GetString() == PurchasingLicence.Code && m.GetProperty("status").GetString() == "enabled");
        }

        using (var refused = await unit.SendAsync("/api/v1/suites/procurement/about", nobody))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        // Past the Foundation plan's 7 days of grace, still within the suite plan's 30: the suite's module still works.
        time.SetUtcNow(new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero));
        await unit.PublishAsync(Document(Ends));
        using (var about = await unit.JsonAsync("/api/v1/suites/procurement/about", administrator))
        {
            var module = Assert.Single(about.RootElement.GetProperty("modules").EnumerateArray());
            Assert.Equal(("enabled", "in_grace"), (module.GetProperty("status").GetString(), module.GetProperty("reason").GetString()));
        }

        // Past the suite plan's grace: the suite's module is switched off, its endpoints refuse, and the shell is told why.
        time.SetUtcNow(new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero));
        await unit.PublishAsync(Document(Ends));
        using (var expired = await unit.SendAsync("/api/v1/suites/procurement/about", administrator))
        {
            Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
            using var problem = await JsonDocument.ParseAsync(await expired.Content.ReadAsStreamAsync(Cancel), cancellationToken: Cancel);
            Assert.Equal(EntitlementErrors.ModuleDisabled, problem.RootElement.GetProperty("error_code").GetString());
        }

        using (var context = await unit.JsonAsync("/api/v1/me/context", administrator))
        {
            var module = context.RootElement.GetProperty("entitlements").GetProperty("modules").EnumerateArray().Single(m => m.GetProperty("code").GetString() == PurchasingLicence.Code);
            Assert.Equal(("disabled", "expired"), (module.GetProperty("status").GetString(), module.GetProperty("reason").GetString()));
        }
    }

    [Fact]
    [Proves("TF-244 AC2", "ADR-0059")]
    public async Task AUnitWithoutTheSuiteAnswersNotFoundAndNamesNoSuiteAndNoneOfItsModules()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero));
        await using var unit = await Unit.StartAsync(fixture, time, withSuite: false, Ids.New());
        var administrator = await unit.PersonWithAsync(SystemRoles.TenantAdministrator);

        // Even with the suite's module in its entitlements: what the unit does not run, it does not show.
        await unit.PublishAsync(Document(Ends));

        using (var about = await unit.SendAsync("/api/v1/suites/procurement/about", administrator))
        {
            Assert.Equal(HttpStatusCode.NotFound, about.StatusCode);
        }

        using var context = await unit.JsonAsync("/api/v1/me/context", administrator);
        var entitlements = context.RootElement.GetProperty("entitlements");
        Assert.Empty(entitlements.GetProperty("suites").EnumerateArray());
        Assert.DoesNotContain(entitlements.GetProperty("modules").EnumerateArray(), m => m.GetProperty("code").GetString() == PurchasingLicence.Code);
    }

    /// <summary>
    /// The document the control plane writes for a tenant with a Foundation plan of 7 days' grace and the suite's
    /// Essentials plan of 30: the suite's module carries its own plan's grace days.
    /// </summary>
    private static EntitlementsDocument Document(DateOnly endsOn) => new(
        EntitlementsDocument.CurrentFormat,
        "echo",
        3,
        "standard",
        7,
        DateTimeOffset.UnixEpoch,
        [new ModuleEntitlement(PurchasingLicence.Code, EntitlementStatus.Enabled, SubscriptionType.Platform, null, Start, endsOn, 30, ProcurementSuite.ShortName)],
        [],
        [new SuitePlan(ProcurementSuite.ShortName, ProcurementSuite.EssentialsPlan, 30)]);

    /// <summary>A tenant's unit in memory: the Foundation's modules this test needs and, when asked, the suite, migrated and seeded.</summary>
    private sealed class Unit : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _client;
        private readonly ControlledSource _source;

        private Unit(WebApplication app, ControlledSource source, TestPrincipals principals)
        {
            _app = app;
            _source = source;
            Principals = principals;
            _client = app.GetTestClient();
        }

        public TestPrincipals Principals { get; }

        public static async Task<Unit> StartAsync(PurchasingFixture fixture, FakeTimeProvider time, bool withSuite, Guid job)
        {
            var database = await fixture.Postgres.CreateDatabaseAsync($"about_{Ids.New():N}"[..20], Cancel);
            var source = new ControlledSource();
            var principals = new TestPrincipals();
            var settings = new Dictionary<string, string?>
            {
                [InstalledSuites.ConfigurationKey] = withSuite ? ProcurementSuite.ShortName : null,
                ["Seed:JobId"] = job.ToString("D"),
                ["Release:Version"] = "2026.10.30.7",
            };

            // As the fleet's jobs do before the unit starts: migrate, then seed, with the system as the actor.
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var suites = InstalledSuites.From(configuration, [PurchasingModule.Suite()]);
            var commands = new ServiceCollection();
            commands.AddSingleton<TimeProvider>(time);
            commands.AddSingleton<ICurrentPrincipal>(SystemPrincipal.Instance);
            commands.AddSingleton<IEntitlementSource>(source);
            commands.AddSingleton(new TenantContext(TenantId.Parse("echo"), ["echo.localhost"]));
            commands.AddSingleton<IConfiguration>(configuration);
            commands.AddLogging();
            commands.AddIronDatabase(database);
            commands.AddModules(configuration, Modules(suites), suites);
            await using (var provider = commands.BuildServiceProvider())
            {
                Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["migrate"], Cancel));
                Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["seed"], Cancel));
            }

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(settings);
            builder.Services.AddSingleton<TimeProvider>(time);
            builder.Services.AddSingleton<IEntitlementSource>(source);
            builder.Services.AddSingleton<IPrincipals>(principals);
            builder.Services.AddSingleton(new TenantContext(TenantId.Parse("echo"), ["echo.localhost"]));
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddScoped<ICurrentPrincipal, HeaderPrincipal>();
            builder.Services.AddIronApi();
            builder.Services.AddIronDatabase(database);
            builder.Services.AddModules(builder.Configuration, Modules(suites), suites);
            var app = builder.Build();
            app.UseIronTelemetry();
            app.UseIronApi();
            app.MapModules();
            await app.StartAsync(Cancel);
            return new Unit(app, source, principals);
        }

        /// <summary>The Foundation's modules the About page and the context need, and the unit's suites.</summary>
        private static IReadOnlyList<IModule> Modules(IReadOnlyList<SuiteDefinition> suites) =>
            InstalledSuites.Compose([new AuditModule(), new EventsModule(), new AuthorizationModule(), new EntitlementsModule(), new FlagsModule()], suites);

        public async Task<Guid> PersonWithAsync(string role)
        {
            var principal = Principals.Person("Ada");
            await using var scope = _app.Services.CreateAsyncScope();
            Assert.True((await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(principal, role, Cancel)).IsSuccess);
            return principal;
        }

        public async Task PublishAsync(EntitlementsDocument document)
        {
            _source.Document = document;
            Assert.True(await _app.Services.GetRequiredService<EntitlementRefresher>().RefreshOnceAsync(Cancel));
        }

        public async Task<HttpResponseMessage> SendAsync(string path, Guid principal)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
            request.Headers.Add(HeaderPrincipal.HeaderName, principal.ToString());
            return await _client.SendAsync(request, Cancel);
        }

        public async Task<JsonDocument> JsonAsync(string path, Guid principal)
        {
            using var response = await SendAsync(path, principal);
            var body = await response.Content.ReadAsStringAsync(Cancel);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} answered {(int)response.StatusCode}: {body}");
            return JsonDocument.Parse(body);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.DisposeAsync();
        }
    }

    /// <summary>The store as the test drives it: the document it answers.</summary>
    private sealed class ControlledSource : IEntitlementSource
    {
        public EntitlementsDocument? Document { get; set; }

        public string Describe() => "the test store";

        public Task<EntitlementsDocument?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Document);
    }

    /// <summary>The people of the test by name; Identity provides them in a unit.</summary>
    private sealed class TestPrincipals : IPrincipals
    {
        private readonly Dictionary<Guid, PrincipalSummary> _known = [];

        public Guid Person(string name)
        {
            var id = Ids.New();
            _known[id] = new PrincipalSummary(id, PrincipalKind.User, name, null);
            return id;
        }

        public Task<IReadOnlyDictionary<Guid, PrincipalSummary>> FindAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, PrincipalSummary>>(ids.Where(_known.ContainsKey).ToDictionary(id => id, id => _known[id]));

        public Task<IReadOnlyList<Guid>> HomedAtAsync(IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    /// <summary>A test principal taken from a request header; Identity provides the real one.</summary>
    private sealed class HeaderPrincipal(IHttpContextAccessor accessor) : ICurrentPrincipal
    {
        public const string HeaderName = "X-Test-Principal";

        public Guid Id => Guid.TryParse(accessor.HttpContext?.Request.Headers[HeaderName].ToString(), out var id) ? id : Guid.Empty;
    }
}
