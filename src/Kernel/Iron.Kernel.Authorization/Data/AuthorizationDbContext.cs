using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Iron.Kernel.Authorization.Data;

/// <summary>The <c>kernel_authz</c> schema: roles, role assignments and the organisation tree (TF-032 to TF-034).</summary>
[Schema("kernel_authz")]
public sealed class AuthorizationDbContext(DbContextOptions<AuthorizationDbContext> options) : ModuleDbContext(options)
{
    public DbSet<Role> Roles => Set<Role>();

    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();

    public DbSet<OrganisationUnit> OrganisationUnits => Set<OrganisationUnit>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Role>(role =>
        {
            role.Property(r => r.Name).HasMaxLength(Role.NameMaxLength);
            role.HasIndex(r => r.Name).IsUnique();
            role.Property(r => r.Description).HasMaxLength(Role.DescriptionMaxLength);
            role.Property(r => r.PrincipalType).HasConversion<string>().HasMaxLength(20);
        });

        modelBuilder.Entity<RoleAssignment>(assignment =>
        {
            assignment.HasOne<Role>().WithMany().HasForeignKey(a => a.RoleId).OnDelete(DeleteBehavior.Cascade);
            assignment.Property(a => a.ScopeKind).HasMaxLength(20);
            assignment.Property(a => a.ScopeRecordType).HasMaxLength(RoleScope.RecordTypeMaxLength);
            assignment.HasIndex(a => a.PrincipalId);
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
    }
}

/// <summary>How <c>dotnet ef migrations add</c> builds the context without a running host (see the developer guide).</summary>
public sealed class AuthorizationDbContextFactory : IDesignTimeDbContextFactory<AuthorizationDbContext>
{
    public AuthorizationDbContext CreateDbContext(string[] args) => new(DesignTime.OptionsFor<AuthorizationDbContext>());
}
