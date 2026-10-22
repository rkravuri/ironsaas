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

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
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
