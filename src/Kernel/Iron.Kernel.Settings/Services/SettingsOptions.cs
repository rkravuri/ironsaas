namespace Iron.Kernel.Settings.Services;

/// <summary>Bound from the <c>Settings</c> section (ADR-0036). The limits are the requirement's; a value above one is cut to it.</summary>
public sealed class SettingsOptions
{
    public const string SectionName = "Settings";

    /// <summary>How often each process reads the values again: 30 seconds, as A4's refresh, and never more (TF-059 AC2).</summary>
    public int RefreshSeconds { get; set; } = 30;

    /// <summary>How long the values may fail to refresh before the alert (TF-059 AC4).</summary>
    public int AlertAfterMinutes { get; set; } = 5;

    /// <summary>How long a secret read from the store is kept: five minutes, and never more (TF-059 AC3).</summary>
    public int SecretCacheSeconds { get; set; } = 300;

    /// <summary>How long the first read may hold up the start of the host before it serves on the defaults.</summary>
    public int StartTimeoutSeconds { get; set; } = 5;

    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(Math.Clamp(RefreshSeconds, 1, 30));

    public TimeSpan AlertAfter => TimeSpan.FromMinutes(Math.Clamp(AlertAfterMinutes, 1, 5));

    public TimeSpan SecretLifetime => TimeSpan.FromSeconds(Math.Clamp(SecretCacheSeconds, 1, 300));
}
