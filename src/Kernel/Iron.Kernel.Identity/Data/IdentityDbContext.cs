using Iron.Kernel.Data.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Iron.Kernel.Identity.Data;

/// <summary>The <c>identity</c> schema: principals and sessions (TF-010, TF-020).</summary>
[Schema("identity")]
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : ModuleDbContext(options)
{
    public DbSet<Principal> Principals => Set<Principal>();

    public DbSet<Session> Sessions => Set<Session>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Principal>(principal =>
        {
            principal.Property(p => p.LoginId).HasMaxLength(Principal.LoginIdMaxLength);
            principal.HasIndex(p => p.LoginId).IsUnique();
            principal.Property(p => p.Type).HasConversion<string>().HasMaxLength(20);
            principal.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
            principal.HasIndex(p => p.Status);
            principal.Property(p => p.DisplayName).HasMaxLength(Principal.DisplayNameMaxLength);
            principal.Property(p => p.Email).HasMaxLength(254);
            principal.Property(p => p.Locale).HasMaxLength(16);
            principal.Property(p => p.TimeZone).HasMaxLength(64);
            principal.Property(p => p.InvitationTokenHash).HasMaxLength(64);
            principal.HasIndex(p => p.InvitationTokenHash).IsUnique();
        });

        modelBuilder.Entity<Session>(session =>
        {
            session.HasOne<Principal>().WithMany().HasForeignKey(s => s.PrincipalId).OnDelete(DeleteBehavior.Cascade);
            session.Property(s => s.TokenHash).HasMaxLength(64);
            session.HasIndex(s => s.TokenHash).IsUnique();
            session.HasIndex(s => s.PrincipalId);
            session.Property(s => s.EndReason).HasMaxLength(20);
            session.HasIndex(s => new { s.EndedAt, s.LastActivityAt });
        });
    }
}

/// <summary>How <c>dotnet ef migrations add</c> builds the context without a running host (see the developer guide).</summary>
public sealed class IdentityDbContextFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args) => new(DesignTime.OptionsFor<IdentityDbContext>());
}
