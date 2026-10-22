namespace Iron.Kernel.Identity.Services;

/// <summary>
/// Bound from the <c>Identity</c> configuration section. The session limits and an invitation's validity are tenant
/// settings since A6 (IdentitySettings, ADR-0036); the session keys here are only the system default.
/// </summary>
public sealed class IdentityOptions
{
    public const string SectionName = "Identity";

    public SessionOptions Session { get; set; } = new();

    public PasswordOptions Password { get; set; } = new();

    public Argon2Options Argon2 { get; set; } = new();

    public LocationOptions Locations { get; set; } = new();

    /// <summary>The bundled location database (ADR-0049): where the API image carries it; without a file, sessions have no location.</summary>
    public sealed class LocationOptions
    {
        public string? DatabasePath { get; set; } = "/app/locations/dbip-city-lite.mmdb";
    }

    /// <summary>
    /// The key that encrypts the second factors' secrets at rest (TF-073, ADR-0047): 32 random bytes in base64, from the
    /// unit's secret (<c>Identity:FactorKey</c>), which the fleet writes when it creates the unit. Never in a file or a log.
    /// </summary>
    public string? FactorKey { get; set; }

    public sealed class SessionOptions
    {
        /// <summary>The cookie that holds the session token.</summary>
        public string CookieName { get; set; } = "iron.session";

        /// <summary>The cookie of an impersonation, over the impersonator's own session, which it leaves as it was (ADR-0049).</summary>
        public string ImpersonationCookieName { get; set; } = "iron.impersonation";

        /// <summary>The system default of <c>identity.session.idle_minutes</c>: what a session takes while the tenant has set nothing.</summary>
        public int IdleMinutes { get; set; } = 60;

        /// <summary>The system default of <c>identity.session.absolute_hours</c>.</summary>
        public int AbsoluteHours { get; set; } = 12;

        /// <summary>last_activity_at is written at most once per this many seconds, to keep one row write per minute, not per request.</summary>
        public int TouchSeconds { get; set; } = 60;

        public TimeSpan Idle => TimeSpan.FromMinutes(IdleMinutes);

        public TimeSpan Absolute => TimeSpan.FromHours(AbsoluteHours);

        public TimeSpan Touch => TimeSpan.FromSeconds(TouchSeconds);
    }

    /// <summary>The password policy is the tenant's settings since T2 (IdentitySettings, ADR-0046); only the upper bound is here.</summary>
    public sealed class PasswordOptions
    {
        /// <summary>The most characters a password may have: enough for any passphrase, and a bound on the hashing work.</summary>
        public int MaximumLength { get; set; } = 1024;
    }

    /// <summary>Argon2id parameters (TF-011). The defaults follow current OWASP guidance; tests lower them for speed.</summary>
    public sealed class Argon2Options
    {
        public int MemoryKb { get; set; } = 65536;

        public int Iterations { get; set; } = 3;

        public int Parallelism { get; set; } = 1;
    }
}
