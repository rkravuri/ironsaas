using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Authorization.Services;
using Iron.Kernel.Data.Sharing;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Authorization.Tests;

/// <summary>
/// Merging units against PostgreSQL (TF-223 AC1, ADR-0038): a preview by module that changes nothing, then one
/// transaction that moves records, assignments, shares and child units, audits all of it and leaves the unit merged;
/// the root, a unit below and an inactive unit are refused; and reactivation, which a merged unit is refused.
/// </summary>
[Collection(AuthorizationCollection.Name)]
public class UnitMergeTests(AuthorizationFixture fixture)
{
    [Fact]
    public async Task ThePreviewListsWhatWouldMoveByModuleAndTheMergeMovesItAllInOneAuditedTransaction()
    {
        await using var admin = fixture.Scope(SystemPrincipal.SystemId);
        var merge = await MergeSetupAsync(admin);

        var preview = await admin.Get<UnitMergeService>().PreviewAsync(merge.Branch.Id, merge.Head.Id, TestContext.Current.CancellationToken);

        Assert.True(preview.IsSuccess, preview.IsFailure ? preview.Error.Message : null);
        var parts = preview.Value.Parts;
        Assert.Equal([UnitMergeParts.Records, UnitMergeParts.Assignments, UnitMergeParts.Shares, UnitMergeParts.Children], parts.Select(p => p.Code));
        var records = parts.Single(p => p.Code == UnitMergeParts.Records);
        Assert.Equal(("probe", ProbeRecords.CrateType, "Crates", 2), (records.Module, records.RecordType, records.RecordTypeName, records.Count));
        var assignments = parts.Single(p => p.Code == UnitMergeParts.Assignments);
        Assert.Equal(1, assignments.Count);
        Assert.Single(assignments.Conflicts);
        Assert.Equal(1, parts.Single(p => p.Code == UnitMergeParts.Shares).Count);
        Assert.Equal(["Desk"], parts.Single(p => p.Code == UnitMergeParts.Children).Items);
        Assert.Equal(2, await admin.Get<ProbeDbContext>().Crates.CountAsync(c => c.OwningOrganisationId == merge.Branch.Id, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(admin.Audit.Events, e => e.Action == AuthorizationAuditEvents.UnitMerged);

        var merged = await admin.Get<UnitMergeService>().MergeAsync(merge.Branch.Id, merge.Head.Id, TestContext.Current.CancellationToken);

        Assert.True(merged.IsSuccess, merged.IsFailure ? merged.Error.Message : null);
        await using var after = fixture.Scope(SystemPrincipal.SystemId);
        var probe = after.Get<ProbeDbContext>();
        Assert.Equal(0, await probe.Crates.CountAsync(c => c.OwningOrganisationId == merge.Branch.Id, TestContext.Current.CancellationToken));
        Assert.Equal(3, await probe.Crates.CountAsync(c => c.OwningOrganisationId == merge.Head.Id, TestContext.Current.CancellationToken));
        var authz = after.Get<AuthorizationDbContext>();
        var branch = await authz.OrganisationUnits.AsNoTracking().SingleAsync(u => u.Id == merge.Branch.Id, TestContext.Current.CancellationToken);
        Assert.Equal((UnitStatus.Inactive, merge.Head.Id), (branch.Status, branch.MergedIntoId));
        var desk = await authz.OrganisationUnits.AsNoTracking().SingleAsync(u => u.Id == merge.Desk.Id, TestContext.Current.CancellationToken);
        Assert.Equal((merge.Head.Id, merge.Head.Path + OrganisationUnit.Segment(desk.Id) + "/"), (desk.ParentId, desk.Path));
        Assert.Equal(0, await authz.RoleAssignments.CountAsync(a => a.ScopeId == merge.Branch.Id, TestContext.Current.CancellationToken));
        Assert.Equal(RoleScope.Unit(merge.Head.Id), (await authz.RoleAssignments.AsNoTracking().SingleAsync(a => a.PrincipalId == merge.Clerk, TestContext.Current.CancellationToken)).ToScope());
        Assert.Single(await authz.RoleAssignments.AsNoTracking().Where(a => a.PrincipalId == merge.Lead).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal([merge.Head.Id], await after.Get<DataDbContext>().Shares.Where(s => s.RecordId == merge.SharedCrate).Select(s => s.UnitId).ToListAsync(TestContext.Current.CancellationToken));

        // One event lists everything; the child unit's move is audited as a move.
        var audited = Assert.Single(admin.Audit.Events, e => e.Action == AuthorizationAuditEvents.UnitMerged);
        Assert.Equal(merge.Branch.Id, audited.RecordId);
        Assert.Equal(merge.Head.Id.ToString("D"), audited.Details!["into_unit_id"]);
        Assert.Equal(2, audited.Details[$"records:probe:{ProbeRecords.CrateType}"].Split(',').Length);
        Assert.Equal(merge.Desk.Id.ToString("D"), audited.Details[UnitMergeParts.Children]);
        Assert.True(audited.Details.ContainsKey("assignments_dropped"));
        Assert.Contains(admin.Audit.Events, e => e.Action == AuthorizationAuditEvents.UnitMoved && e.RecordId == merge.Desk.Id);

        // The clerk's role at the branch holds at the head office now.
        var evaluator = after.Get<IPermissionEvaluator>();
        var atHead = new AccessRequest(merge.Clerk, ProbeRecords.CratesRead, "GET /x", "/x") { Scope = AccessScope.Unit(merge.Head.Id) };
        Assert.True((await evaluator.EvaluateAsync(atHead, TestContext.Current.CancellationToken)).Allowed);

        // A merged unit stays merged.
        var again = await after.Get<UnitMergeService>().MergeAsync(merge.Branch.Id, merge.Head.Id, TestContext.Current.CancellationToken);
        var reactivated = await after.Get<OrganisationUnitService>().ReactivateAsync(merge.Branch.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AuthorizationErrors.UnitMerged, again.Error.Code);
        Assert.Equal(AuthorizationErrors.UnitMerged, reactivated.Error.Code);
    }

    [Fact]
    public async Task TheRootAUnitBelowItselfAndAnInactiveUnitAreRefusedAndNothingMoves()
    {
        await using var admin = fixture.Scope(SystemPrincipal.SystemId);
        var units = admin.Get<OrganisationUnitService>();
        var root = await OrganisationUnitServiceTests.RootAsync(admin.Scope);
        var branch = await OrganisationUnitServiceTests.CreateAsync(units, root.Id, "Branch");
        var desk = await OrganisationUnitServiceTests.CreateAsync(units, branch.Id, "Desk");
        var closed = await OrganisationUnitServiceTests.CreateAsync(units, root.Id, "Closed");
        Assert.True((await units.DeactivateAsync(closed.Id, TestContext.Current.CancellationToken)).IsSuccess);
        var merges = admin.Get<UnitMergeService>();

        var theRoot = await merges.PreviewAsync(root.Id, branch.Id, TestContext.Current.CancellationToken);
        var itself = await merges.MergeAsync(branch.Id, branch.Id, TestContext.Current.CancellationToken);
        var below = await merges.MergeAsync(branch.Id, desk.Id, TestContext.Current.CancellationToken);
        var inactive = await merges.MergeAsync(desk.Id, closed.Id, TestContext.Current.CancellationToken);
        var unknown = await merges.PreviewAsync(Ids.New(), branch.Id, TestContext.Current.CancellationToken);

        Assert.Equal(AuthorizationErrors.UnitIsRoot, theRoot.Error.Code);
        Assert.Equal(AuthorizationErrors.UnitMergeIntoOwn, itself.Error.Code);
        Assert.Equal(AuthorizationErrors.UnitMergeIntoOwn, below.Error.Code);
        Assert.Equal(AuthorizationErrors.UnitInactive, inactive.Error.Code);
        Assert.Equal(AuthorizationErrors.UnitNotFound, unknown.Error.Code);
        var still = await admin.Get<AuthorizationDbContext>().OrganisationUnits.AsNoTracking().SingleAsync(u => u.Id == desk.Id, TestContext.Current.CancellationToken);
        Assert.Equal((UnitStatus.Active, branch.Id), (still.Status, still.ParentId));
    }

    [Fact]
    public async Task ADeactivatedUnitIsReactivatedUnderAnActiveParentAndAudited()
    {
        await using var admin = fixture.Scope(SystemPrincipal.SystemId);
        var units = admin.Get<OrganisationUnitService>();
        var root = await OrganisationUnitServiceTests.RootAsync(admin.Scope);
        var region = await OrganisationUnitServiceTests.CreateAsync(units, root.Id, "Region");
        var office = await OrganisationUnitServiceTests.CreateAsync(units, region.Id, "Office");
        Assert.True((await units.DeactivateAsync(office.Id, TestContext.Current.CancellationToken)).IsSuccess);

        var reactivated = await units.ReactivateAsync(office.Id, TestContext.Current.CancellationToken);

        Assert.True(reactivated.IsSuccess);
        Assert.Equal(UnitStatus.Active, reactivated.Value.Status);
        Assert.Contains(admin.Audit.Events, e => e.Action == AuthorizationAuditEvents.UnitReactivated && e.RecordId == office.Id);

        Assert.True((await units.DeactivateAsync(office.Id, TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await units.DeactivateAsync(region.Id, TestContext.Current.CancellationToken)).IsSuccess);
        var underInactive = await units.ReactivateAsync(office.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AuthorizationErrors.UnitParentInactive, underInactive.Error.Code);
    }

    /// <summary>
    /// A head office and a branch with a desk under it. The branch owns two crates; a crate of the head office is shared
    /// with the branch. A clerk holds a role at the branch (it moves); a lead holds the same role, with the units below,
    /// at both (the branch's is dropped).
    /// </summary>
    private async Task<(OrganisationUnit Head, OrganisationUnit Branch, OrganisationUnit Desk, Guid Clerk, Guid Lead, Guid SharedCrate)> MergeSetupAsync(TestScope admin)
    {
        var units = admin.Get<OrganisationUnitService>();
        var root = await OrganisationUnitServiceTests.RootAsync(admin.Scope);
        var head = await OrganisationUnitServiceTests.CreateAsync(units, root.Id, "Head office");
        var branch = await OrganisationUnitServiceTests.CreateAsync(units, root.Id, "Branch");
        var desk = await OrganisationUnitServiceTests.CreateAsync(units, branch.Id, "Desk");

        var probe = admin.Get<ProbeDbContext>();
        var shared = new Crate(head.Id, "shared with the branch");
        probe.Crates.AddRange(new Crate(branch.Id, "branch one"), new Crate(branch.Id, "branch two"), shared);
        await probe.SaveChangesAsync(TestContext.Current.CancellationToken);
        var data = admin.Get<DataDbContext>();
        data.Shares.Add(new RecordShare(ProbeRecords.CrateType, shared.Id, branch.Id));
        await data.SaveChangesAsync(TestContext.Current.CancellationToken);

        var role = new Role(RoleServiceTests.Code(), "Crate reader", string.Empty, isSystem: false, PrincipalKind.User, [ProbeRecords.CratesRead.Name]);
        var authz = admin.Get<AuthorizationDbContext>();
        authz.Roles.Add(role);
        await authz.SaveChangesAsync(TestContext.Current.CancellationToken);
        var clerk = fixture.Principals.Add(homeUnit: branch.Id);
        var lead = fixture.Principals.Add(homeUnit: head.Id);
        await RoleServiceTests.AssignAsync(admin.Scope, clerk, role.Code, RoleScope.Unit(branch.Id));
        await RoleServiceTests.AssignAsync(admin.Scope, lead, role.Code, RoleScope.Unit(branch.Id, includeChildren: true));
        await RoleServiceTests.AssignAsync(admin.Scope, lead, role.Code, RoleScope.Unit(head.Id, includeChildren: true));
        return (head, branch, desk, clerk, lead, shared.Id);
    }
}
