using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Iron.Kernel.Authorization.Data;

/// <summary>
/// The <c>kernel_authz</c> schema: roles, role assignments and the organisation tree (TF-032 to TF-034), delegations
/// (TF-037) and access reviews (TF-038).
/// </summary>
[Schema("kernel_authz")]
public sealed class AuthorizationDbContext(DbContextOptions<AuthorizationDbContext> options) : ModuleDbContext(options)
{
    public DbSet<Role> Roles => Set<Role>();

    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();

    public DbSet<OrganisationUnit> OrganisationUnits => Set<OrganisationUnit>();

    public DbSet<Delegation> Delegations => Set<Delegation>();

    public DbSet<AccessReview> AccessReviews => Set<AccessReview>();

    public DbSet<AccessReviewUnit> AccessReviewUnits => Set<AccessReviewUnit>();

    public DbSet<AccessReviewItem> AccessReviewItems => Set<AccessReviewItem>();

    public DbSet<AccessReviewDecision> AccessReviewDecisions => Set<AccessReviewDecision>();

    public DbSet<AccessReviewEscalation> AccessReviewEscalations => Set<AccessReviewEscalation>();

    public DbSet<ConflictRule> ConflictRules => Set<ConflictRule>();

    public DbSet<ConflictReportRun> ConflictReportRuns => Set<ConflictReportRun>();

    public DbSet<ConflictReportEntry> ConflictReportEntries => Set<ConflictReportEntry>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ConflictRule>(rule =>
        {
            rule.Property(r => r.Name).HasMaxLength(ConflictRule.NameMaxLength);
            rule.Property(r => r.FirstKind).HasMaxLength(20);
            rule.Property(r => r.FirstValue).HasMaxLength(ConflictRule.ValueMaxLength);
            rule.Property(r => r.SecondKind).HasMaxLength(20);
            rule.Property(r => r.SecondValue).HasMaxLength(ConflictRule.ValueMaxLength);
            rule.Property(r => r.Mode).HasMaxLength(20);
            rule.Property(r => r.Reason).HasMaxLength(ConflictRule.ReasonMaxLength);
        });

        modelBuilder.Entity<ConflictReportRun>(run => run.Property(r => r.Trigger).HasMaxLength(20));

        modelBuilder.Entity<ConflictReportEntry>(entry =>
        {
            entry.Property(e => e.RuleName).HasMaxLength(ConflictRule.NameMaxLength);
            entry.Property(e => e.Mode).HasMaxLength(20);
            entry.Property(e => e.PrincipalName).HasMaxLength(200);
            entry.Property(e => e.FirstRole).HasMaxLength(Role.NameMaxLength);
            entry.Property(e => e.FirstScopeKind).HasMaxLength(30);
            entry.Property(e => e.FirstUnitName).HasMaxLength(OrganisationUnit.NameMaxLength);
            entry.Property(e => e.FirstSource).HasMaxLength(20);
            entry.Property(e => e.SecondRole).HasMaxLength(Role.NameMaxLength);
            entry.Property(e => e.SecondScopeKind).HasMaxLength(30);
            entry.Property(e => e.SecondUnitName).HasMaxLength(OrganisationUnit.NameMaxLength);
            entry.Property(e => e.SecondSource).HasMaxLength(20);
            entry.Property(e => e.UnitName).HasMaxLength(OrganisationUnit.NameMaxLength);
            entry.HasIndex(e => e.PrincipalId);
            entry.HasIndex(e => e.RuleId);
            entry.HasIndex(e => e.FirstAssignmentId);
            entry.HasIndex(e => e.SecondAssignmentId);
        });

        modelBuilder.Entity<Role>(role =>
        {
            role.Property(r => r.Code).HasMaxLength(Role.CodeMaxLength);
            role.HasIndex(r => r.Code).IsUnique();
            role.Property(r => r.Name).HasMaxLength(Role.NameMaxLength);
            role.Property(r => r.Description).HasMaxLength(Role.DescriptionMaxLength);
            role.Property(r => r.PrincipalType).HasConversion<string>().HasMaxLength(20);
        });

        modelBuilder.Entity<RoleAssignment>(assignment =>
        {
            assignment.HasOne<Role>().WithMany().HasForeignKey(a => a.RoleId).OnDelete(DeleteBehavior.Cascade);
            assignment.Property(a => a.ScopeKind).HasMaxLength(20);
            assignment.Property(a => a.ScopeRecordType).HasMaxLength(RoleScope.RecordTypeMaxLength);
            assignment.Property(a => a.Source).HasMaxLength(RoleAssignment.SourceMaxLength);
            assignment.HasIndex(a => a.PrincipalId);
            assignment.HasIndex(a => a.Source);
            assignment.HasIndex(a => new { a.PrincipalId, a.RoleId, a.ScopeKind, a.ScopeId, a.ScopeRecordType }).IsUnique().AreNullsDistinct(false);
        });

        modelBuilder.Entity<OrganisationUnit>(unit =>
        {
            unit.Property(u => u.Name).HasMaxLength(OrganisationUnit.NameMaxLength);
            unit.Property(u => u.Code).HasMaxLength(OrganisationUnit.CodeMaxLength);
            unit.HasIndex(u => u.Code).IsUnique();
            unit.Property(u => u.Kind).HasMaxLength(OrganisationUnit.KindMaxLength);
            unit.Property(u => u.Status).HasConversion<string>().HasMaxLength(20);
            unit.Property(u => u.Path).HasMaxLength(OrganisationUnit.PathMaxLength);

            // A subtree is a path prefix (ADR-0030): the pattern operator class lets LIKE 'prefix%' use the index.
            unit.HasIndex(u => u.Path).IsUnique().HasOperators("varchar_pattern_ops");
            unit.HasIndex(u => u.ParentId);
            unit.HasOne<OrganisationUnit>().WithMany().HasForeignKey(u => u.ParentId).OnDelete(DeleteBehavior.Restrict);

            // One root per tenant: the only unit at depth 0.
            unit.HasIndex(u => u.Depth).IsUnique().HasFilter("depth = 0").HasDatabaseName("ux_organisation_units_root");
            unit.Ignore(u => u.IsRoot);
            unit.Ignore(u => u.IsActive);
        });

        modelBuilder.Entity<Delegation>(delegation =>
        {
            delegation.ToTable("delegations");
            delegation.Property(d => d.Reason).HasMaxLength(Delegation.ReasonMaxLength);
            delegation.Property(d => d.EndReason).HasMaxLength(Delegation.ReasonMaxLength);
            delegation.HasIndex(d => new { d.DelegatorId, d.EndsAt });
            delegation.HasIndex(d => new { d.DelegateId, d.EndsAt });
            delegation.Ignore(d => d.Until);
            delegation.Ignore(d => d.CoversAll);
        });

        modelBuilder.Entity<AccessReview>(review =>
        {
            review.ToTable("access_reviews");
            review.Property(r => r.Name).HasMaxLength(AccessReview.NameMaxLength);
            review.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            review.HasIndex(r => r.Status);
            review.Ignore(r => r.IsOpen);
        });

        modelBuilder.Entity<AccessReviewUnit>(unit =>
        {
            unit.ToTable("access_review_units");
            unit.HasOne<AccessReview>().WithMany().HasForeignKey(u => u.ReviewId).OnDelete(DeleteBehavior.Restrict);
            unit.HasIndex(u => new { u.ReviewId, u.UnitId }).IsUnique();
        });

        modelBuilder.Entity<AccessReviewItem>(item =>
        {
            item.ToTable("access_review_items");
            item.HasOne<AccessReview>().WithMany().HasForeignKey(i => i.ReviewId).OnDelete(DeleteBehavior.Restrict);
            item.Property(i => i.PrincipalName).HasMaxLength(200);
            item.Property(i => i.RoleCode).HasMaxLength(Role.CodeMaxLength);
            item.Property(i => i.RoleName).HasMaxLength(Role.NameMaxLength);
            item.Property(i => i.ScopeKind).HasMaxLength(20);
            item.Property(i => i.TagCode).HasMaxLength(32);
            item.Property(i => i.TagName).HasMaxLength(100);

            // Items taken before visibility tags (V1) list none.
            item.Property(i => i.HeldTagCodes).HasDefaultValueSql("'{}'");
            item.Property(i => i.HeldTagNames).HasDefaultValueSql("'{}'");
            item.Property(i => i.UnitName).HasMaxLength(OrganisationUnit.NameMaxLength);
            item.HasIndex(i => new { i.ReviewId, i.AssignmentId }).IsUnique();
            item.HasIndex(i => new { i.ReviewId, i.UnitId });
        });

        // One decision per item, whoever decides it: the unique index is the last word when two reviewers race.
        modelBuilder.Entity<AccessReviewDecision>(decision =>
        {
            decision.ToTable("access_review_decisions");
            decision.HasOne<AccessReviewItem>().WithMany().HasForeignKey(d => d.ItemId).OnDelete(DeleteBehavior.Restrict);
            decision.Property(d => d.Decision).HasConversion<string>().HasMaxLength(20);
            decision.Property(d => d.Comment).HasMaxLength(AccessReviewDecision.CommentMaxLength);
            decision.HasIndex(d => d.ItemId).IsUnique();
            decision.HasIndex(d => d.ReviewId);
        });

        modelBuilder.Entity<AccessReviewEscalation>(escalation =>
        {
            escalation.ToTable("access_review_escalations");
            escalation.HasOne<AccessReview>().WithMany().HasForeignKey(e => e.ReviewId).OnDelete(DeleteBehavior.Restrict);
            escalation.HasIndex(e => new { e.ReviewId, e.UnitId }).IsUnique();
        });
    }
}

/// <summary>How <c>dotnet ef migrations add</c> builds the context without a running host (see the developer guide).</summary>
public sealed class AuthorizationDbContextFactory : IDesignTimeDbContextFactory<AuthorizationDbContext>
{
    public AuthorizationDbContext CreateDbContext(string[] args) => new(DesignTime.OptionsFor<AuthorizationDbContext>());
}
