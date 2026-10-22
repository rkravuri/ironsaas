using Iron.Kernel.Data.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Iron.Kernel.Identity.Data;

/// <summary>
/// The <c>identity</c> schema: principals and sessions (TF-010, TF-020), password history, the failed sign-ins of each
/// login identifier, and each person's security events (TF-012, TF-017, TF-019); second factors, recovery codes and the
/// state of authentications under way (TF-013, TF-248, ADR-0047); single sign-on providers and the people linked to them
/// (TF-014, ADR-0048).
/// </summary>
[Schema("kernel_identity")]
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : ModuleDbContext(options)
{
    public DbSet<Principal> Principals => Set<Principal>();

    public DbSet<Session> Sessions => Set<Session>();

    public DbSet<PasswordHistoryEntry> PasswordHistory => Set<PasswordHistoryEntry>();

    public DbSet<LoginThrottle> LoginThrottles => Set<LoginThrottle>();

    public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();

    public DbSet<Factor> Factors => Set<Factor>();

    public DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();

    public DbSet<AuthenticationChallenge> AuthenticationChallenges => Set<AuthenticationChallenge>();

    public DbSet<SsoProvider> SsoProviders => Set<SsoProvider>();

    public DbSet<SsoLink> SsoLinks => Set<SsoLink>();

    public DbSet<TenantRevocation> TenantRevocations => Set<TenantRevocation>();

    private const int DeviceNamesMaxLength = Services.DeviceNames.MaxLength;

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Factor>(factor =>
        {
            factor.HasOne<Principal>().WithMany().HasForeignKey(f => f.PrincipalId).OnDelete(DeleteBehavior.Cascade);
            factor.Property(f => f.Kind).HasMaxLength(Factor.KindMaxLength);
            factor.Property(f => f.Name).HasMaxLength(Factor.NameMaxLength);
            factor.HasIndex(f => f.PrincipalId);
            factor.HasIndex(f => f.CredentialId).IsUnique();
            factor.HasIndex(f => f.UserHandle);
        });

        modelBuilder.Entity<RecoveryCode>(code =>
        {
            code.HasOne<Principal>().WithMany().HasForeignKey(c => c.PrincipalId).OnDelete(DeleteBehavior.Cascade);
            code.Property(c => c.CodeHash).HasMaxLength(64);
            code.HasIndex(c => c.CodeHash).IsUnique();
            code.HasIndex(c => c.PrincipalId);
        });

        modelBuilder.Entity<AuthenticationChallenge>(challenge =>
        {
            challenge.HasOne<Principal>().WithMany().HasForeignKey(c => c.PrincipalId).OnDelete(DeleteBehavior.Cascade);
            challenge.HasOne<Session>().WithMany().HasForeignKey(c => c.SessionId).OnDelete(DeleteBehavior.Cascade);
            challenge.Property(c => c.Purpose).HasMaxLength(AuthenticationChallenge.PurposeMaxLength);
            challenge.Property(c => c.TokenHash).HasMaxLength(64);
            challenge.HasIndex(c => c.TokenHash).IsUnique();
            challenge.HasIndex(c => c.SessionId);
            challenge.HasIndex(c => c.ExpiresAt);
            challenge.HasOne<SsoProvider>().WithMany().HasForeignKey(c => c.ProviderId).OnDelete(DeleteBehavior.Cascade);
            challenge.Property(c => c.Exchange).HasColumnType("jsonb");
        });

        modelBuilder.Entity<SsoProvider>(provider =>
        {
            provider.Property(p => p.Name).HasMaxLength(SsoProvider.NameMaxLength);
            provider.HasIndex(p => p.Name).IsUnique();
            provider.Property(p => p.Preset).HasMaxLength(SsoProvider.PresetMaxLength);
            provider.Property(p => p.DiscoveryUrl).HasMaxLength(SsoProvider.DiscoveryUrlMaxLength);
            provider.Property(p => p.ClientId).HasMaxLength(SsoProvider.ClientIdMaxLength);
            provider.Property(p => p.EmailClaim).HasMaxLength(SsoProvider.ClaimMaxLength);
            provider.Property(p => p.NameClaim).HasMaxLength(SsoProvider.ClaimMaxLength);
            provider.Property(p => p.GroupsClaim).HasMaxLength(SsoProvider.ClaimMaxLength);
            provider.Property(p => p.DefaultRoleCode).HasMaxLength(100);
            provider.Property(p => p.RoleMappingsJson).HasColumnName("role_mappings").HasColumnType("jsonb");
            provider.Ignore(p => p.RoleMappings);
            provider.Ignore(p => p.AssignmentSource);
        });

        modelBuilder.Entity<SsoLink>(link =>
        {
            link.HasOne<Principal>().WithMany().HasForeignKey(l => l.PrincipalId).OnDelete(DeleteBehavior.Cascade);
            link.HasOne<SsoProvider>().WithMany().HasForeignKey(l => l.ProviderId).OnDelete(DeleteBehavior.Cascade);
            link.Property(l => l.Subject).HasMaxLength(SsoLink.SubjectMaxLength);
            link.Property(l => l.Email).HasMaxLength(254);
            link.HasIndex(l => new { l.ProviderId, l.Subject }).IsUnique();
            link.HasIndex(l => new { l.ProviderId, l.PrincipalId }).IsUnique();
            link.HasIndex(l => l.PrincipalId);
        });

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
            session.Property(s => s.SecondFactor).HasMaxLength(20);
            session.HasOne<SsoProvider>().WithMany().HasForeignKey(s => s.ProviderId).OnDelete(DeleteBehavior.SetNull);
            session.HasIndex(s => new { s.EndedAt, s.LastActivityAt });
            session.Property(s => s.Method).HasMaxLength(SessionMethods.MaxLength).HasDefaultValue(SessionMethods.Password);
            session.Property(s => s.Browser).HasMaxLength(DeviceNamesMaxLength);
            session.Property(s => s.Platform).HasMaxLength(DeviceNamesMaxLength);
            session.Property(s => s.City).HasMaxLength(100);
            session.Property(s => s.Country).HasMaxLength(100);
            session.Property(s => s.ImpersonatorName).HasMaxLength(Session.ImpersonatorNameMaxLength);
            session.Property(s => s.ImpersonationReason).HasMaxLength(Session.ImpersonationReasonMaxLength);
            session.HasIndex(s => s.ImpersonatorId);
            session.HasIndex(s => s.CreatedAt);
            session.HasIndex(s => s.EndedAt);
        });

        modelBuilder.Entity<TenantRevocation>(revocation =>
        {
            revocation.Property(r => r.Reason).HasMaxLength(TenantRevocation.ReasonMaxLength);
            revocation.HasIndex(r => r.RevokedAt);
        });
    }
}

/// <summary>How <c>dotnet ef migrations add</c> builds the context without a running host (see the developer guide).</summary>
public sealed class IdentityDbContextFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args) => new(DesignTime.OptionsFor<IdentityDbContext>());
}
