using Iron.Kernel.Data.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Iron.Kernel.Authorization.Data;

/// <summary>The <c>authz</c> schema: roles and role assignments (TF-032, TF-033).</summary>
[Schema("kernel_authz")]
public sealed class AuthorizationDbContext(DbContextOptions<AuthorizationDbContext> options) : ModuleDbContext(options)
{
    public DbSet<Role> Roles => Set<Role>();

    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();

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
            assignment.HasIndex(a => a.PrincipalId);
            assignment.HasIndex(a => new { a.PrincipalId, a.RoleId, a.ScopeKind, a.ScopeId }).IsUnique().AreNullsDistinct(false);
        });
    }
}

/// <summary>How <c>dotnet ef migrations add</c> builds the context without a running host (see the developer guide).</summary>
public sealed class AuthorizationDbContextFactory : IDesignTimeDbContextFactory<AuthorizationDbContext>
{
    public AuthorizationDbContext CreateDbContext(string[] args) => new(DesignTime.OptionsFor<AuthorizationDbContext>());
}
