using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Flags;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Audit.Services;
using Iron.Kernel.Authorization;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Authorization.Services;
using Iron.Kernel.Compliance.Contracts;
using Iron.Kernel.Compliance.Data;
using Iron.Kernel.Compliance.Services;
using Iron.Kernel.Data;
using Iron.Kernel.Files;
using Iron.Kernel.Settings;
using Iron.Kernel.Settings.Services;
using Iron.Kernel.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Compliance.Tests;

/// <summary>
/// Evidence packs (S3 T8, TF-067, ADR-0052): a pack for a period holds its five documents, each as CSV and as a readable
/// page with the tenant and the period; its manifest's hashes recompute and its chain head matches its anchor; the state
/// at the period's end is rebuilt from the field history; asking, making and downloading are audited with the requester;
/// and a person without the permission is refused.
/// </summary>
[Collection(EvidenceCollection.Name)]
public class EvidencePackTests(EvidenceFixture fixture)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-067 AC1", "ADR-0052")]
    public async Task APackForAPeriodHoldsItsFiveDocumentsEachAsAPageAndCsvWithTheTenantAndThePeriod()
    {
        var (provider, actor, time) = Setup(new DateTimeOffset(2031, 3, 1, 9, 0, 0, TimeSpan.Zero));
        await using var _ = provider;
        var requester = fixture.People.Add("Rhea Requester");
        var from = new DateTimeOffset(2031, 2, 1, 0, 0, 0, TimeSpan.Zero);

        var pack = await MakeAsync(provider, actor, requester, from, new DateTimeOffset(2031, 3, 1, 0, 0, 0, TimeSpan.Zero));
        using var zip = await DownloadAsync(provider, actor, requester, pack.Id);

        Assert.Equal(EvidencePackStates.Ready, pack.Status);
        foreach (var document in EvidenceDocuments.All)
        {
            var folder = EvidenceDocuments.FolderOf(document);
            var page = Read(zip, $"{folder}/{document}.html");
            Assert.Contains("<dt>Tenant</dt><dd>echo</dd>", page, StringComparison.Ordinal);
            Assert.Contains("2031-02-01T00:00:00.000000Z up to 2031-03-01T00:00:00.000000Z", page, StringComparison.Ordinal);
            var csvs = zip.Entries.Where(e => e.FullName.StartsWith(folder + "/", StringComparison.Ordinal) && e.FullName.EndsWith(".csv", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(csvs);
            Assert.All(csvs, csv => Assert.Contains("# Tenant: echo. Period: 2031-02-01T00:00:00.000000Z up to 2031-03-01T00:00:00.000000Z UTC.", Read(zip, csv.FullName), StringComparison.Ordinal));
        }

        Assert.Contains("Backups start with the first Azure deployment", Read(zip, $"{EvidenceDocuments.FolderOf(EvidenceDocuments.BackupDrills)}/backup_drills.html"), StringComparison.Ordinal);
        Assert.Contains("1-access_list/roles_and_scopes.csv", zip.Entries.Select(e => e.FullName));
        Assert.Contains("2-administrative_actions/administrative_actions.csv", zip.Entries.Select(e => e.FullName));
        Assert.Contains("3-security_settings/settings.csv", zip.Entries.Select(e => e.FullName));
        Assert.Contains("4-chain_verification/verifications.csv", zip.Entries.Select(e => e.FullName));
    }

    [Fact]
    [Proves("TF-067 AC1", "ADR-0052")]
    public async Task TheManifestListsEveryFileWithAHashThatRecomputesTheRequesterAndTheChainHeadMatchingItsAnchor()
    {
        var (provider, actor, time) = Setup(new DateTimeOffset(2031, 5, 10, 9, 0, 0, TimeSpan.Zero));
        await using var _ = provider;
        var requester = fixture.People.Add("Mira Manifest");
        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAuditWriter>().WriteAsync(new AuditEvent(AuthorizationAuditEvents.RoleCreated, AuditOutcome.Success, requester, "manifest-probe"), Cancel);
            await scope.ServiceProvider.GetRequiredService<AuditSealer>().SealAsync(Cancel);
        }

        AuditSeal seal;
        await using (var scope = provider.CreateAsyncScope())
        {
            seal = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Seals.AsNoTracking().OrderByDescending(s => s.LastPosition).FirstAsync(Cancel);
        }

        var anchorId = Ids.New();
        fixture.ControlPlane.Seal = (seal.LastPosition, seal.LastHash, anchorId, seal.CreatedAt);
        var pack = await MakeAsync(provider, actor, requester, new DateTimeOffset(2031, 5, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2031, 5, 10, 0, 0, 0, TimeSpan.Zero));
        using var zip = await DownloadAsync(provider, actor, requester, pack.Id);
        using var manifest = JsonDocument.Parse(Read(zip, EvidencePackWriter.ManifestName));
        var root = manifest.RootElement;

        var listed = root.GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("path").GetString()!, f => f.GetProperty("sha256").GetString()!);
        Assert.Equal(zip.Entries.Select(e => e.FullName).Where(n => n != EvidencePackWriter.ManifestName).Order(StringComparer.Ordinal), listed.Keys.Order(StringComparer.Ordinal));
        Assert.All(listed, file => Assert.Equal(file.Value, Convert.ToHexStringLower(SHA256.HashData(Bytes(zip, file.Key)))));
        Assert.Equal(EvidencePackWriter.Format, root.GetProperty("format").GetString());
        Assert.Equal(requester, root.GetProperty("requested_by").GetProperty("id").GetGuid());
        Assert.Equal("Mira Manifest", root.GetProperty("requested_by").GetProperty("name").GetString());

        var head = root.GetProperty("chain_head");
        var anchor = head.GetProperty("anchor");
        string? hashAtHead;
        await using (var scope = provider.CreateAsyncScope())
        {
            var position = head.GetProperty("position").GetInt64();
            hashAtHead = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking().Where(e => e.ChainPosition == position).Select(e => e.Hash).SingleAsync(Cancel);
        }

        Assert.Equal(seal.LastPosition, head.GetProperty("position").GetInt64());
        Assert.Equal(anchorId, anchor.GetProperty("id").GetGuid());
        Assert.Equal(anchor.GetProperty("hash").GetString(), head.GetProperty("hash").GetString());
        Assert.Equal(hashAtHead, head.GetProperty("hash").GetString());
        Assert.Equal((seal.LastPosition, anchorId), (pack.ChainPosition!.Value, pack.AnchorId!.Value));
        Assert.Equal(pack.ManifestSha256, Convert.ToHexStringLower(SHA256.HashData(Bytes(zip, EvidencePackWriter.ManifestName))));
    }

    [Fact]
    [Proves("TF-067 AC1", "TF-062 AC4", "ADR-0052")]
    public async Task TheAccessListAndTheSettingsAreAsTheyStoodAtThePeriodsEnd()
    {
        var start = new DateTimeOffset(2032, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var (provider, actor, time) = Setup(start.AddHours(1));
        await using var _ = provider;
        var requester = fixture.People.Add("Ada Auditor");
        var administrator = fixture.People.Add("Al Administrator");
        var (before, removedLater, grantedLater) = (fixture.People.Add("Bea Before"), fixture.People.Add("Rex Removed-later"), fixture.People.Add("Lou Later"));
        var role = $"evidence-{Ids.New().ToString("N")[^12..]}";
        await using (var scope = provider.CreateAsyncScope())
        {
            var assignments = scope.ServiceProvider.GetRequiredService<IRoleAssignments>();
            Assert.True((await assignments.AssignAsync(administrator, SystemRoles.TenantAdministrator, Cancel)).IsSuccess);
            actor.Id = administrator;
            Assert.True((await scope.ServiceProvider.GetRequiredService<RoleService>().CreateAsync(role, new RoleInput("Evidence probe", string.Empty, PrincipalKind.User, ["authz.units.read"]), Cancel)).IsSuccess);
            Assert.True((await assignments.AssignAsync(before, role, Cancel)).IsSuccess);
            Assert.True((await assignments.AssignAsync(removedLater, role, Cancel)).IsSuccess);
            Assert.True((await scope.ServiceProvider.GetRequiredService<SettingsWriter>().SetAsync(ComplianceSettings.LaterSecondFactor.Key, SettingPlace.Tenant, JsonSerializer.SerializeToElement(true), Cancel)).IsSuccess);
        }

        var end = start.AddDays(1);
        time.SetUtcNow(start.AddDays(2));
        await using (var scope = provider.CreateAsyncScope())
        {
            actor.Id = administrator;
            var context = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
            var removed = await context.RoleAssignments.AsNoTracking().SingleAsync(a => a.PrincipalId == removedLater, Cancel);
            Assert.True((await scope.ServiceProvider.GetRequiredService<AssignmentService>().RemoveAsync(removed.Id, Cancel)).IsSuccess);
            Assert.True((await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(grantedLater, role, Cancel)).IsSuccess);
            Assert.True((await scope.ServiceProvider.GetRequiredService<SettingsWriter>().SetAsync(ComplianceSettings.LaterSecondFactor.Key, SettingPlace.Tenant, JsonSerializer.SerializeToElement(false), Cancel)).IsSuccess);
        }

        time.SetUtcNow(start.AddDays(3));
        var pack = await MakeAsync(provider, actor, requester, start, end);
        using var zip = await DownloadAsync(provider, actor, requester, pack.Id);

        var held = Read(zip, "1-access_list/roles_and_scopes.csv");
        Assert.Contains($"Bea Before,person,{role}", held, StringComparison.Ordinal);
        Assert.Contains($"Rex Removed-later,person,{role}", held, StringComparison.Ordinal);
        Assert.DoesNotContain("Lou Later", held, StringComparison.Ordinal);
        var changes = Read(zip, "1-access_list/assignment_changes.csv");
        Assert.Contains($",granted,Bea Before,{role},", changes, StringComparison.Ordinal);
        Assert.DoesNotContain("Lou Later", changes, StringComparison.Ordinal);
        Assert.DoesNotContain(",removed,", changes, StringComparison.Ordinal);
        Assert.Contains($"{role},Evidence probe,User,no,authz.units.read", Read(zip, "1-access_list/roles.csv"), StringComparison.Ordinal);
        var settings = Read(zip, "3-security_settings/settings.csv").Split('\n');
        Assert.Contains(settings, line => line.StartsWith($"{ComplianceSettings.LaterSecondFactor.Key},", StringComparison.Ordinal) && line.Contains(",true,the tenant,signing_policy,", StringComparison.Ordinal));
    }

    [Fact]
    [Proves("TF-067 AC1", "ADR-0052")]
    public async Task TheAdministrativeActionsAreTheEventsOfAdministrativeTypesInThePeriodWithActorAndReason()
    {
        var start = new DateTimeOffset(2033, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var (provider, actor, time) = Setup(start.AddHours(2));
        await using var _ = provider;
        var requester = fixture.People.Add("Ida Inspector");
        var administrator = fixture.People.Add("Ola Operations");
        await using (var scope = provider.CreateAsyncScope())
        {
            var audit = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
            await audit.WriteAsync(new AuditEvent(AuthorizationAuditEvents.RoleDeleted, AuditOutcome.Success, administrator, "old-role", RecordType: "role", RecordId: Ids.New(), Reason: "No longer used."), Cancel);
            await audit.WriteAsync(new AuditEvent(AuthorizationAuditEvents.AccessExplained, AuditOutcome.Success, administrator, "not administrative"), Cancel);
        }

        time.SetUtcNow(start.AddDays(2));
        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAuditWriter>().WriteAsync(new AuditEvent(AuthorizationAuditEvents.RoleCreated, AuditOutcome.Success, administrator, "after-the-period"), Cancel);
        }

        var pack = await MakeAsync(provider, actor, requester, start, start.AddDays(1));
        using var zip = await DownloadAsync(provider, actor, requester, pack.Id);
        var actions = Read(zip, "2-administrative_actions/administrative_actions.csv");

        Assert.Contains($",{AuthorizationAuditEvents.RoleDeleted},Role deleted,success,{administrator:D},Ola Operations,", actions, StringComparison.Ordinal);
        Assert.Contains("No longer used.", actions, StringComparison.Ordinal);
        Assert.DoesNotContain("not administrative", actions, StringComparison.Ordinal);
        Assert.DoesNotContain("after-the-period", actions, StringComparison.Ordinal);
    }

    [Fact]
    [Proves("TF-067 AC2", "ADR-0052")]
    public async Task AskingMakingAndDownloadingAPackAreAuditedNamingTheRequester()
    {
        var (provider, actor, time) = Setup(new DateTimeOffset(2034, 2, 3, 9, 0, 0, TimeSpan.Zero));
        await using var _ = provider;
        var requester = fixture.People.Add("Reg Requester");
        var colleague = fixture.People.Add("Cora Colleague");

        var pack = await MakeAsync(provider, actor, requester, new DateTimeOffset(2034, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2034, 2, 1, 0, 0, 0, TimeSpan.Zero));
        using (await DownloadAsync(provider, actor, colleague, pack.Id))
        {
        }

        await using var scope = provider.CreateAsyncScope();
        var events = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking().Where(e => e.RecordId == pack.Id).OrderBy(e => e.CreatedAt).ToListAsync(Cancel);
        var asked = Assert.Single(events, e => e.EventType == ComplianceAuditEvents.EvidencePackRequested);
        var made = Assert.Single(events, e => e.EventType == ComplianceAuditEvents.EvidencePackMade);
        var downloaded = Assert.Single(events, e => e.EventType == ComplianceAuditEvents.EvidencePackDownloaded);

        Assert.Equal(requester, asked.ActorId);
        Assert.Equal((SystemPrincipal.SystemId, (Guid?)requester), (made.ActorId, made.OnBehalfOfId));
        Assert.Equal(colleague, downloaded.ActorId);
        Assert.Equal(requester.ToString("D"), JsonDocument.Parse(downloaded.Details!).RootElement.GetProperty("requested_by").GetString());
        Assert.Equal(pack.Sha256, JsonDocument.Parse(made.Details!).RootElement.GetProperty("sha256").GetString());
    }

    [Fact]
    [Proves("TF-067 AC2", "ADR-0052")]
    public async Task ADownloadLinkWorksOnceForItsPersonWithinFiveMinutes()
    {
        var (provider, actor, time) = Setup(new DateTimeOffset(2034, 8, 3, 9, 0, 0, TimeSpan.Zero));
        await using var _ = provider;
        var requester = fixture.People.Add("Lin Links");
        var other = fixture.People.Add("Otto Other");
        var pack = await MakeAsync(provider, actor, requester, new DateTimeOffset(2034, 8, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2034, 8, 2, 0, 0, 0, TimeSpan.Zero));

        actor.Id = requester;
        var first = await LinkAsync(provider, pack.Id);
        var late = await LinkAsync(provider, pack.Id);
        actor.Id = other;
        var notTheirs = await OpenAsync(provider, Token(first));
        actor.Id = requester;
        var opened = await OpenAsync(provider, Token(first));
        var again = await OpenAsync(provider, Token(first));
        time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        var expired = await OpenAsync(provider, Token(late));

        Assert.Equal(ComplianceErrors.EvidenceLinkInvalid, notTheirs.Error.Code);
        Assert.True(opened.IsSuccess, opened.IsFailure ? opened.Error.ToString() : null);
        Assert.Equal(ComplianceErrors.EvidenceLinkInvalid, again.Error.Code);
        Assert.Equal(ComplianceErrors.EvidenceLinkInvalid, expired.Error.Code);
    }

    [Fact]
    [Proves("TF-067 AC3", "ADR-0052")]
    public async Task APersonWithoutTheEvidencePermissionIsRefusedAndOneWithItIsHeard()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentPrincipal, HeaderActor>();
        builder.Services.AddSingleton(new TenantContext(TenantId.Parse("echo"), ["echo.localhost"]));
        builder.Services.AddIronApi();
        builder.Services.AddIronDatabase(fixture.ConnectionString);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:ConnectionString"] = fixture.StorageConnectionString,
            ["Storage:Container"] = EvidenceFixture.Container,
        });
        builder.Services.AddModules(builder.Configuration, [new AuditModule(), new AuthorizationModule(), new SettingsModule(), new FilesModule(), new ComplianceModule()]);
        builder.Services.AddScoped<IEntitlementGate, Entitled>();
        builder.Services.AddScoped<IFlagGate, Entitled>();
        builder.Services.AddSingleton<IPrincipals>(fixture.People);
        await using var app = builder.Build();
        app.UseIronTelemetry();
        app.UseIronApi();
        app.MapModules();
        await app.StartAsync(Cancel);
        using var client = app.GetTestClient();
        var (reader, auditor) = (fixture.People.Add("Rae Reader"), fixture.People.Add("Aud Auditor"));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(reader, SystemRoles.ReadOnly, Cancel)).IsSuccess);
            Assert.True((await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(auditor, SystemRoles.TenantAdministrator, Cancel)).IsSuccess);
        }

        var body = new { from = DateTimeOffset.UtcNow.AddDays(-30), to = DateTimeOffset.UtcNow };
        using var refused = await SendAsync(client, reader, HttpMethod.Post, "/api/v1/evidence-packs", body);
        using var listRefused = await SendAsync(client, reader, HttpMethod.Get, "/api/v1/evidence-packs", null);
        using var heard = await SendAsync(client, auditor, HttpMethod.Post, "/api/v1/evidence-packs", body);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, listRefused.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, heard.StatusCode);
        using var made = JsonDocument.Parse(await heard.Content.ReadAsStringAsync(Cancel));
        Assert.Equal(EvidencePackStates.Requested, made.RootElement.GetProperty("status").GetString());

        // The pack asked for here is the worker's; this test makes it, so the collection's next test starts with none waiting.
        await using var cleanup = app.Services.CreateAsyncScope();
        await cleanup.ServiceProvider.GetRequiredService<EvidencePacks>().BuildNextAsync(Cancel);
    }

    private (ServiceProvider Provider, EvidenceActor Actor, FakeTimeProvider Time) Setup(DateTimeOffset now)
    {
        var time = new FakeTimeProvider(now);
        var actor = new EvidenceActor();
        fixture.ControlPlane.Seal = null;
        fixture.ControlPlane.Verifications.Clear();
        return (fixture.BuildServices(time, actor), actor, time);
    }

    private static async Task<EvidencePack> MakeAsync(IServiceProvider provider, EvidenceActor actor, Guid requester, DateTimeOffset from, DateTimeOffset to)
    {
        Guid id;
        actor.Id = requester;
        await using (var scope = provider.CreateAsyncScope())
        {
            var asked = await scope.ServiceProvider.GetRequiredService<EvidencePacks>().RequestAsync(from, to, Cancel);
            Assert.True(asked.IsSuccess, asked.IsFailure ? asked.Error.ToString() : null);
            id = asked.Value.Id;
        }

        // The worker makes it, as the system.
        actor.Id = SystemPrincipal.SystemId;
        await using (var scope = provider.CreateAsyncScope())
        {
            while (await scope.ServiceProvider.GetRequiredService<EvidencePacks>().BuildNextAsync(Cancel))
            {
            }
        }

        await using var read = provider.CreateAsyncScope();
        var pack = (await read.ServiceProvider.GetRequiredService<EvidencePacks>().FindAsync(id, Cancel))!;
        Assert.True(pack.Status == EvidencePackStates.Ready, pack.Failure);
        return pack;
    }

    private static async Task<ZipArchive> DownloadAsync(IServiceProvider provider, EvidenceActor actor, Guid person, Guid packId)
    {
        actor.Id = person;
        var link = await LinkAsync(provider, packId);
        var opened = await OpenAsync(provider, Token(link));
        Assert.True(opened.IsSuccess, opened.IsFailure ? opened.Error.ToString() : null);
        return new ZipArchive(new MemoryStream(opened.Value.Content), ZipArchiveMode.Read);
    }

    private static async Task<EvidenceLink> LinkAsync(IServiceProvider provider, Guid packId)
    {
        await using var scope = provider.CreateAsyncScope();
        var link = await scope.ServiceProvider.GetRequiredService<EvidencePacks>().LinkAsync(packId, Cancel);
        Assert.True(link.IsSuccess, link.IsFailure ? link.Error.ToString() : null);
        return link.Value;
    }

    private static async Task<Result<OpenedPack>> OpenAsync(IServiceProvider provider, string token)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<EvidencePacks>().OpenAsync(token, Cancel);
    }

    private static string Token(EvidenceLink link) => link.Url[EvidencePacks.DownloadPath.Length..];

    private static byte[] Bytes(ZipArchive zip, string path)
    {
        using var stream = zip.GetEntry(path)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string Read(ZipArchive zip, string path) => Encoding.UTF8.GetString(Bytes(zip, path));

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, Guid person, HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(HeaderActor.HeaderName, person.ToString("D"));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request, Cancel);
    }

    private sealed class HeaderActor(IHttpContextAccessor accessor) : ICurrentPrincipal
    {
        public const string HeaderName = "X-Test-Principal";

        public Guid Id => Guid.TryParse(accessor.HttpContext?.Request.Headers[HeaderName].ToString(), out var id) ? id : SystemPrincipal.SystemId;
    }

    /// <summary>Every module and feature entitled and no kill switch on: the tests prove Compliance's own rules.</summary>
    private sealed class Entitled : IEntitlementGate, IFlagGate
    {
        public ValueTask<EntitlementDecision> CheckAsync(EntitlementRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(EntitlementDecision.Allow);

        public ValueTask<FlagDecision> CheckAsync(FlagRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(FlagDecision.Allow);
    }
}
