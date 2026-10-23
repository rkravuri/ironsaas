using Iron.Kernel.History;

namespace Iron.Kernel.Data.History;

/// <summary>
/// Writes a save's record changes into the audit trail (TF-062, ADR-0051), inside the save's transaction. The Audit module
/// implements it; a host without it records no history, as the data tests' own context does.
/// </summary>
public interface IChangeRecorder
{
    Task RecordAsync(IReadOnlyList<RecordChange> changes, CancellationToken cancellationToken);
}

/// <summary>
/// The reason for the changes of this scope, when the person gave one (TF-062): a service that takes a reason sets it
/// before it saves, and every change event of the scope carries it.
/// </summary>
public interface IChangeReason
{
    string? Reason { get; set; }
}

public sealed class ChangeReason : IChangeReason
{
    public string? Reason { get; set; }
}
