using Iron.Kernel.Data.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Iron.Kernel.Tenancy.Data;

/// <summary>The <c>kernel_tenancy</c> schema: the tenant registry and its hostnames (TF-001), plans, tenants' entitlements and their history (TF-040 to TF-045).</summary>
[Schema("kernel_tenancy")]
public sealed class TenancyDbContext(DbContextOptions<TenancyDbContext> options) : ModuleDbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<TenantHostname> Hostnames => Set<TenantHostname>();

    public DbSet<Plan> Plans => Set<Plan>();

    public DbSet<TenantEntitlement> Entitlements => Set<TenantEntitlement>();

    public DbSet<EntitlementChange> EntitlementHistory => Set<EntitlementChange>();

    public DbSet<FlagRule> FlagRules => Set<FlagRule>();

    public DbSet<FlagRuleChange> FlagHistory => Set<FlagRuleChange>();

    public DbSet<FlagPublication> FlagPublications => Set<FlagPublication>();

    public DbSet<BreakGlassAccess> BreakGlassAccesses => Set<BreakGlassAccess>();

    public DbSet<PlatformDocument> PlatformDocuments => Set<PlatformDocument>();

    public DbSet<UnitClient> UnitClients => Set<UnitClient>();

    public DbSet<AuditAnchorRecord> AuditAnchors => Set<AuditAnchorRecord>();

    public DbSet<AuditVerificationRecord> AuditVerifications => Set<AuditVerificationRecord>();

    public DbSet<AuditVerificationRequest> AuditVerificationRequests => Set<AuditVerificationRequest>();

    public DbSet<AuditRestoreRecord> AuditRestores => Set<AuditRestoreRecord>();

    public DbSet<TenantKeyRecord> TenantKeys => Set<TenantKeyRecord>();

    public DbSet<KeyRotationRecord> KeyRotations => Set<KeyRotationRecord>();

    public DbSet<Incident> Incidents => Set<Incident>();

    public DbSet<IncidentTenant> IncidentTenants => Set<IncidentTenant>();

    public DbSet<IncidentEntry> IncidentEntries => Set<IncidentEntry>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        // Incidents (TF-079 AC2, ADR-0056): the incident, the tenants it affects with their notices, and its timeline, written once.
        modelBuilder.Entity<Incident>(incident =>
        {
            incident.ToTable("incidents");
            incident.Property(i => i.Summary).HasMaxLength(Incident.SummaryMaxLength);
            incident.Property(i => i.Lead).HasMaxLength(Incident.NameMaxLength);
            incident.Property(i => i.Communications).HasMaxLength(Incident.NameMaxLength);
            incident.Property(i => i.Scribe).HasMaxLength(Incident.NameMaxLength);
            incident.Property(i => i.Status).HasMaxLength(10);
            incident.Property(i => i.CloseSummary).HasMaxLength(Incident.TextMaxLength);
            incident.Property(i => i.WhatHappened).HasMaxLength(Incident.TextMaxLength);
            incident.Property(i => i.Why).HasMaxLength(Incident.TextMaxLength);
            incident.Property(i => i.WhatChanges).HasMaxLength(Incident.TextMaxLength);
            incident.HasIndex(i => new { i.Status, i.ReviewDueAt });
        });

        modelBuilder.Entity<IncidentTenant>(tenant =>
        {
            tenant.ToTable("incident_tenants");
            tenant.Property(t => t.TenantSlug).HasMaxLength(63);
            tenant.Property(t => t.Notice).HasMaxLength(Incident.TextMaxLength);
            tenant.HasIndex(t => new { t.IncidentId, t.TenantSlug }).IsUnique();
        });

        modelBuilder.Entity<IncidentEntry>(entry =>
        {
            entry.ToTable("incident_entries");
            entry.Property(e => e.ByName).HasMaxLength(Incident.NameMaxLength);
            entry.Property(e => e.Kind).HasMaxLength(20);
            entry.Property(e => e.Text).HasMaxLength(Incident.TextMaxLength);
            entry.Property(e => e.TenantSlug).HasMaxLength(63);
            entry.HasIndex(e => new { e.IncidentId, e.At });
        });

        // The tenants' audit chains (TF-061, ADR-0050): the anchors and the verifications are written once.
        modelBuilder.Entity<UnitClient>(client =>
        {
            client.Property(c => c.TenantSlug).HasMaxLength(63);
            client.HasIndex(c => c.TenantSlug).IsUnique();
            client.HasIndex(c => c.PrincipalId).IsUnique();
        });

        modelBuilder.Entity<AuditAnchorRecord>(anchor =>
        {
            anchor.ToTable("audit_anchors");
            anchor.Property(a => a.TenantSlug).HasMaxLength(63);
            anchor.Property(a => a.Kind).HasMaxLength(AuditAnchorRecord.KindMaxLength);
            anchor.Property(a => a.Hash).HasMaxLength(AuditAnchorRecord.HashLength).IsFixedLength();
            anchor.Property(a => a.Segment).HasDefaultValue(1);
            anchor.HasIndex(a => new { a.TenantSlug, a.Segment, a.Kind, a.Position }).IsUnique();
        });

        modelBuilder.Entity<TenantKeyRecord>(key =>
        {
            key.ToTable("tenant_keys");
            key.Property(k => k.TenantSlug).HasMaxLength(63);
            key.Property(k => k.Key).HasMaxLength(TenantKeyRecord.KeyMaxLength);
            key.HasIndex(k => new { k.TenantSlug, k.Key }).IsUnique();
        });

        modelBuilder.Entity<KeyRotationRecord>(rotation =>
        {
            rotation.ToTable("key_rotations");
            rotation.Property(r => r.TenantSlug).HasMaxLength(63);
            rotation.Property(r => r.Key).HasMaxLength(TenantKeyRecord.KeyMaxLength);
            rotation.Property(r => r.Trigger).HasMaxLength(20);
            rotation.Property(r => r.RequestedByName).HasMaxLength(KeyRotationRecord.NameMaxLength);
            rotation.Property(r => r.Reason).HasMaxLength(KeyRotationRecord.ReasonMaxLength);
            rotation.Property(r => r.State).HasMaxLength(20);
            rotation.Property(r => r.Error).HasMaxLength(KeyRotationRecord.ErrorMaxLength);
            rotation.HasIndex(r => new { r.TenantSlug, r.Key, r.State });
            rotation.HasIndex(r => r.State);
        });

        modelBuilder.Entity<AuditRestoreRecord>(restore =>
        {
            restore.ToTable("audit_restores");
            restore.Property(r => r.TenantSlug).HasMaxLength(63);
            restore.Property(r => r.PreviousHash).HasMaxLength(AuditAnchorRecord.HashLength).IsFixedLength();
            restore.Property(r => r.Ticket).HasMaxLength(AuditRestoreRecord.TicketMaxLength);
            restore.Property(r => r.Reason).HasMaxLength(AuditRestoreRecord.ReasonMaxLength);
            restore.Property(r => r.AcceptedByName).HasMaxLength(200);
            restore.HasIndex(r => new { r.TenantSlug, r.Segment }).IsUnique();
        });

        modelBuilder.Entity<AuditVerificationRecord>(verification =>
        {
            verification.ToTable("audit_verifications");
            verification.Property(v => v.TenantSlug).HasMaxLength(63);
            verification.Property(v => v.Result).HasMaxLength(AuditVerificationRecord.ResultMaxLength);
            verification.Property(v => v.ToHash).HasMaxLength(AuditAnchorRecord.HashLength).IsFixedLength();
            verification.Property(v => v.Problems).HasColumnType("jsonb");
            verification.HasIndex(v => new { v.TenantSlug, v.CreatedAt });
        });

        modelBuilder.Entity<AuditVerificationRequest>(request =>
        {
            request.Property(r => r.TenantSlug).HasMaxLength(63);
            request.HasIndex(r => r.VerificationId);
        });

        modelBuilder.Entity<BreakGlassAccess>(access =>
        {
            access.ToTable("break_glass_accesses");
            access.Property(a => a.TenantSlug).HasMaxLength(63);
            access.Property(a => a.Reason).HasMaxLength(BreakGlassAccess.ReasonMaxLength);
            access.Property(a => a.Ticket).HasMaxLength(BreakGlassAccess.TicketMaxLength);
            access.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
            access.Property(a => a.RequestedByName).HasMaxLength(BreakGlassAccess.NameMaxLength);
            access.Property(a => a.ApprovedByName).HasMaxLength(BreakGlassAccess.NameMaxLength);
            access.Property(a => a.AccountLoginId).HasMaxLength(254);
            access.Property(a => a.EndReason).HasMaxLength(BreakGlassAccess.ReasonMaxLength);
            access.Property(a => a.Review).HasColumnType("jsonb");
            access.Ignore(a => a.ApprovalDeadline);
            access.HasIndex(a => a.Status);
            access.HasIndex(a => new { a.TenantSlug, a.RequestedAt });
        });

        modelBuilder.Entity<PlatformDocument>(document =>
        {
            document.Property(d => d.Key).HasMaxLength(PlatformDocument.KeyMaxLength);
            document.Property(d => d.Title).HasMaxLength(PlatformDocument.TitleMaxLength);
            document.Property(d => d.Body).HasMaxLength(PlatformDocument.BodyMaxLength);
            document.HasIndex(d => new { d.Key, d.Number }).IsUnique();
        });

        modelBuilder.Entity<Tenant>(tenant =>
        {
            tenant.Property(t => t.Slug).HasMaxLength(63);
            tenant.HasIndex(t => t.Slug).IsUnique();
            tenant.Property(t => t.DisplayName).HasMaxLength(Tenant.DisplayNameMaxLength);
            tenant.Property(t => t.Region).HasMaxLength(32);
            tenant.Property(t => t.Plan).HasMaxLength(64);
            tenant.Property(t => t.SizeClass).HasConversion<string>().HasMaxLength(20);
            tenant.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
            tenant.HasIndex(t => t.Status);
            tenant.Property(t => t.AdministratorLoginId).HasMaxLength(254);
            tenant.Property(t => t.AdministratorDisplayName).HasMaxLength(200);
            tenant.Property(t => t.DatabaseName).HasMaxLength(63);
            tenant.Property(t => t.StorageContainer).HasMaxLength(63);
        });

        modelBuilder.Entity<Plan>(plan =>
        {
            plan.Property(p => p.Code).HasMaxLength(Plan.CodeMaxLength);
            plan.HasIndex(p => p.Code).IsUnique();
            plan.Property(p => p.Name).HasMaxLength(Plan.NameMaxLength);
            plan.Property(p => p.ModulesJson).HasColumnName("modules").HasColumnType("jsonb");
            plan.Ignore(p => p.Modules);
        });

        modelBuilder.Entity<TenantEntitlement>(entitlement =>
        {
            entitlement.ToTable("tenant_entitlements");
            entitlement.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Cascade);
            entitlement.Property(e => e.Kind).HasConversion<string>().HasMaxLength(20);
            entitlement.Property(e => e.Code).HasMaxLength(100);
            entitlement.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            entitlement.Property(e => e.SubscriptionType).HasConversion<string>().HasMaxLength(20);
            entitlement.HasIndex(e => new { e.TenantId, e.Kind, e.Code }).IsUnique();
            entitlement.Ignore(e => e.Value);
        });

        modelBuilder.Entity<EntitlementChange>(change =>
        {
            change.ToTable("entitlement_history");
            change.Property(c => c.TenantSlug).HasMaxLength(63);
            change.Property(c => c.Kind).HasMaxLength(20);
            change.Property(c => c.Code).HasMaxLength(100);
            change.Property(c => c.Before).HasColumnType("jsonb");
            change.Property(c => c.After).HasColumnType("jsonb");
            change.Property(c => c.Reason).HasMaxLength(EntitlementChange.ReasonMaxLength);
            change.HasIndex(c => new { c.TenantId, c.CreatedAt });
        });

        modelBuilder.Entity<FlagRule>(rule =>
        {
            rule.ToTable("flag_rules");
            rule.Property(r => r.FlagKey).HasMaxLength(100);
            rule.Property(r => r.Scope).HasConversion<string>().HasMaxLength(20);
            rule.Property(r => r.Region).HasMaxLength(32);
            rule.Property(r => r.TenantSlug).HasMaxLength(63);
            rule.Property(r => r.ConditionKind).HasConversion<string>().HasMaxLength(20);
            rule.Property(r => r.RolloutOf).HasConversion<string>().HasMaxLength(20);
            rule.Property(r => r.Attribute).HasMaxLength(50);
            rule.Property(r => r.ValueJson).HasColumnName("value").HasColumnType("jsonb");
            rule.Property(r => r.Reason).HasMaxLength(FlagRule.ReasonMaxLength);
            rule.Ignore(r => r.StorePath);
            rule.Ignore(r => r.Condition);
            rule.HasIndex(r => new { r.FlagKey, r.Position });
            rule.HasIndex(r => r.TenantSlug);
        });

        modelBuilder.Entity<FlagRuleChange>(change =>
        {
            change.ToTable("flag_rule_history");
            change.Property(c => c.FlagKey).HasMaxLength(100);
            change.Property(c => c.Kind).HasMaxLength(20);
            change.Property(c => c.Before).HasColumnType("jsonb");
            change.Property(c => c.After).HasColumnType("jsonb");
            change.Property(c => c.Reason).HasMaxLength(FlagRule.ReasonMaxLength);
            change.HasIndex(c => new { c.FlagKey, c.Revision }).IsUnique();
        });

        modelBuilder.Entity<FlagPublication>(publication =>
        {
            publication.ToTable("flag_publications");
            publication.Property(p => p.Path).HasMaxLength(100);
            publication.HasIndex(p => p.Path).IsUnique();
        });

        modelBuilder.Entity<TenantHostname>(hostname =>
        {
            hostname.HasOne<Tenant>().WithMany().HasForeignKey(h => h.TenantId).OnDelete(DeleteBehavior.Cascade);
            hostname.Property(h => h.Hostname).HasMaxLength(TenantHostname.MaxLength);
            hostname.HasIndex(h => h.Hostname).IsUnique();
            hostname.HasIndex(h => h.TenantId);
        });
    }
}

/// <summary>How <c>dotnet ef migrations add</c> builds the context without a running host (see the developer guide).</summary>
public sealed class TenancyDbContextFactory : IDesignTimeDbContextFactory<TenancyDbContext>
{
    public TenancyDbContext CreateDbContext(string[] args) => new(DesignTime.OptionsFor<TenancyDbContext>());
}
