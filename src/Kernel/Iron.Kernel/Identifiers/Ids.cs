namespace Iron.Kernel;

/// <summary>
/// Identifiers for records. Foundation uses UUID version 7: random, but ordered by creation time, which keeps
/// database indexes compact (TF-080, ADR-0007). Never use <c>Guid.NewGuid()</c> for a primary key.
/// </summary>
public static class Ids
{
    /// <summary>A new time-ordered identifier for now.</summary>
    public static Guid New() => Guid.CreateVersion7();

    /// <summary>A new time-ordered identifier for the time of the given provider. Use this in code that takes a <see cref="TimeProvider"/>.</summary>
    public static Guid New(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return Guid.CreateVersion7(time.GetUtcNow());
    }
}
