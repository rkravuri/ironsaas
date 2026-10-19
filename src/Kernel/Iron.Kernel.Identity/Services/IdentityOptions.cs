namespace Iron.Kernel.Identity.Services;

/// <summary>Bound from the <c>Identity</c> configuration section. Tenant settings take over in S2.</summary>
public sealed class IdentityOptions
{
    public const string SectionName = "Identity";

    public SessionOptions Session { get; set; } = new();

    public PasswordOptions Password { get; set; } = new();

    public Argon2Options Argon2 { get; set; } = new();

    public InvitationOptions Invitation { get; set; } = new();

    public sealed class SessionOptions
    {
        /// <summary>The cookie that holds the session token.</summary>
        public string CookieName { get; set; } = "iron.session";

        /// <summary>A session ends after this long without a request (TF-021, default 60 minutes).</summary>
        public int IdleMinutes { get; set; } = 60;

        /// <summary>A session ends this long after login whatever happens (TF-021, default 12 hours).</summary>
        public int AbsoluteHours { get; set; } = 12;

        /// <summary>last_activity_at is written at most once per this many seconds, to keep one row write per minute, not per request.</summary>
        public int TouchSeconds { get; set; } = 60;

        public TimeSpan Idle => TimeSpan.FromMinutes(IdleMinutes);

        public TimeSpan Absolute => TimeSpan.FromHours(AbsoluteHours);

        public TimeSpan Touch => TimeSpan.FromSeconds(TouchSeconds);
    }

    public sealed class PasswordOptions
    {
        /// <summary>TF-012: 12 by default. The common-password list and history arrive in S3.</summary>
        public int MinimumLength { get; set; } = 12;

        public int MaximumLength { get; set; } = 1024;
    }

    /// <summary>Argon2id parameters (TF-011). The defaults follow current OWASP guidance; tests lower them for speed.</summary>
    public sealed class Argon2Options
    {
        public int MemoryKb { get; set; } = 65536;

        public int Iterations { get; set; } = 3;

        public int Parallelism { get; set; } = 1;
    }

    public sealed class InvitationOptions
    {
        /// <summary>TF-018: 72 hours by default.</summary>
        public int ValidHours { get; set; } = 72;

        public TimeSpan Validity => TimeSpan.FromHours(ValidHours);
    }
}
