using Iron.Kernel.Data.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Iron.Kernel.Identity.Data;

/// <summary>
/// The <c>identity</c> schema: principals and sessions (TF-010, TF-020), password history, the failed sign-ins of each
/// login identifier, and each person's security events (TF-012, TF-017, TF-019).
/// </summary>
[Schema("kernel_identity")]
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : ModuleDbContext(options)
{
    public DbSet<Principal> Principals => Set<Principal>();

    public DbSet<Session> Sessions => Set<Session>();

    public DbSet<PasswordHistoryEntry> PasswordHistory => Set<PasswordHistoryEntry>();

    public DbSet<LoginThrottle> LoginThrottles => Set<LoginThrottle>();

    public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();

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
            principal.HasIndex(p => p.HomeUnitId);
            principal.Property(p => p.RecoveryTokenHash).HasMaxLength(64);
            principal.HasIndex(p => p.RecoveryTokenHash).IsUnique();
        });

        modelBuilder.Entity<PasswordHistoryEntry>(entry =>
        {
            entry.ToTable("password_history");
            entry.HasOne<Principal>().WithMany().HasForeignKey(e => e.PrincipalId).OnDelete(DeleteBehavior.Cascade);
            entry.Property(e => e.PasswordHash).HasMaxLength(200);
            entry.HasIndex(e => new { e.PrincipalId, e.SetAt });
        });

        modelBuilder.Entity<LoginThrottle>(throttle =>
        {
            throttle.Property(t => t.LoginId).HasMaxLength(Principal.LoginIdMaxLength);
            throttle.HasIndex(t => t.LoginId).IsUnique();
            throttle.HasIndex(t => t.LockedUntil);
        });

        modelBuilder.Entity<SecurityEvent>(securityEvent =>
        {
            securityEvent.HasOne<Principal>().WithMany().HasForeignKey(e => e.PrincipalId).OnDelete(DeleteBehavior.Restrict);
            securityEvent.Property(e => e.Kind).HasMaxLength(SecurityEvent.KindMaxLength);
            securityEvent.Property(e => e.Actor).HasMaxLength(SecurityEvent.ActorMaxLength);
            securityEvent.Property(e => e.ActorName).HasMaxLength(SecurityEvent.ActorNameMaxLength);
            securityEvent.Property(e => e.Reason).HasMaxLength(SecurityEvent.ReasonMaxLength);
            securityEvent.HasIndex(e => new { e.PrincipalId, e.OccurredAt });
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
