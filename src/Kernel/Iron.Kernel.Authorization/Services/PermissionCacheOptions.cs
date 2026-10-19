namespace Iron.Kernel.Authorization.Services;

/// <summary>Bound from the <c>Authorization</c> configuration section.</summary>
public sealed class PermissionCacheOptions
{
    public const string SectionName = "Authorization";
    public const int MaxCacheSeconds = 60;

    /// <summary>How long a principal's permission set is cached. At most 60 seconds (TF-031 AC2); 0 reads the database on every check.</summary>
    public int CacheSeconds { get; set; } = MaxCacheSeconds;
}
