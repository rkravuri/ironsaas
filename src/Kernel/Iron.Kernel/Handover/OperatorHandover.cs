namespace Iron.Kernel.Handover;

/// <summary>
/// What a unit hands over to the operator who asked for it (ADR-0046): an invitation or a recovery link, as the path the
/// shell opens (<c>/invitations#&lt;token&gt;</c>, <c>/recovery#&lt;code&gt;</c>), whom it is for, and when it stops working.
/// </summary>
public sealed record OperatorHandoverItem(string Kind, string Subject, string LinkPath, DateTimeOffset ExpiresAt)
{
    public const string Invitation = "invitation";
    public const string Recovery = "recovery";
}

/// <summary>
/// The one way a link from the operator's path leaves a unit (ADR-0046): into a one-time place in the store that lives
/// as long as the link, never into a command's output, a log line or a trace. The fleet gives an image command the place
/// and a token that can only write there; the control plane gives the link once to the operator who asked and deletes it.
/// </summary>
public interface IOperatorHandover
{
    /// <summary>Whether this process was given a place: the fleet's image commands are; a unit serving requests is not.</summary>
    bool IsAvailable { get; }

    Task DepositAsync(OperatorHandoverItem item, CancellationToken cancellationToken);
}

/// <summary>
/// A process the fleet gave no place: a unit serving requests, a host run from source, a test. The default until the
/// secret store's registration supplies the real one.
/// </summary>
public sealed class NoOperatorHandover : IOperatorHandover
{
    public bool IsAvailable => false;

    public Task DepositAsync(OperatorHandoverItem item, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("No handover place is configured: the fleet sets Handover:Path and Handover:Token for the command it runs.");
}
