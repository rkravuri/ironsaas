namespace Iron.Kernel.Identity.Contracts;

/// <summary>
/// The login identifier of a break-glass access's account, which both planes know (ADR-0049): the control plane names
/// the account it asks the unit to close, the unit makes it. Under a domain nobody can own, so it is never anyone's email.
/// </summary>
public static class BreakGlassAccountNames
{
    public const string Domain = "break-glass.invalid";

    public static string LoginIdOf(Guid accessId) => $"break-glass-{accessId.ToString("N")[..12]}@{Domain}";
}
