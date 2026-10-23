using System.Net;
using System.Text.Json;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;

namespace Iron.Kernel.Identity.Tests;

/// <summary>
/// A tenant document keeps its field-level history (TF-062, ADR-0051, the T7 review): its publication and its retirement
/// are record events under the record type its own events use, so its History shows both, and the Documents page has its id.
/// </summary>
[Collection(IdentityCollection.Name)]
public class DocumentHistoryTests(IdentityFixture fixture)
{
    [Fact]
    [Proves("TF-062 AC1", "ADR-0051")]
    public async Task ADocumentPublishedThenRetiredHasItsCreationAndItsRetirementFieldByField()
    {
        // A tenant of its own: a published document holds every person of the tenant until they accept it.
        await using var host = await UnitHost.StartAsync(fixture, await fixture.CreateTenantDatabaseAsync($"documents_{Ids.New():N}"));
        var (administrator, admin) = await host.AdministratorAsync();
        using var published = await host.SendAsync(HttpMethod.Post, "/api/v1/documents", admin, new { key = "house-rules", title = "House rules", body = "Be kind." });
        Assert.Equal(HttpStatusCode.Created, published.StatusCode);
        using var accepted = await host.SendAsync(HttpMethod.Post, "/api/v1/me/terms/accept", admin, new { documents = new[] { new { source = "tenant", key = "house-rules", version = 1 } } });
        Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var listed = (await UnitHost.JsonAsync(await host.SendAsync(HttpMethod.Get, "/api/v1/documents", admin))).GetProperty("documents").EnumerateArray()
            .Single(d => d.GetProperty("key").GetString() == "house-rules");
        var id = listed.GetProperty("id").GetGuid();

        using var retired = await host.SendAsync(HttpMethod.Post, "/api/v1/documents/house-rules/retire", admin);

        Assert.True(retired.IsSuccessStatusCode, await retired.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var created = Assert.Single(await host.AuditAsync(AuditEvents.RecordCreated), e => e.RecordId == id);
        var changed = Assert.Single(await host.AuditAsync(AuditEvents.RecordChanged), e => e.RecordId == id);
        Assert.Equal((TenantDocument.RecordType, TenantDocument.RecordType), (created.RecordType, changed.RecordType));
        Assert.Contains(Fields(created.Changes), f => f.GetProperty("field").GetString() == "title" && f.GetProperty("new").GetString() == "House rules");
        var field = Assert.Single(Fields(changed.Changes));
        Assert.Equal(("retired", "false", "true"), (field.GetProperty("field").GetString(), field.GetProperty("old").GetString(), field.GetProperty("new").GetString()));
        Assert.Equal(administrator.Id, changed.ActorId);
        Assert.Contains(await host.AuditAsync(IdentityAuditEvents.DocumentRetired), e => e.RecordId == id && e.RecordType == TenantDocument.RecordType);
    }

    private static List<JsonElement> Fields(string? changes) =>
        [.. JsonDocument.Parse(changes ?? "[]").RootElement.EnumerateArray().Select(e => e.Clone())];
}
