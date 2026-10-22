namespace Iron.Kernel;

/// <summary>
/// What someone signed in as another person may never do (TF-025, ADR-0049): confirm who they are, sign, approve as the
/// second person, or change the person's own factors, password, sessions and acceptances. Step-up would let them prove an
/// identity that is not theirs, and a signature must be the real person's (21 CFR Part 11). The recent-authentication
/// check refuses with this error, so every action behind step-up, a signature (T7) and a second approval (T8) is refused
/// too; an endpoint of the person's own sign-in declares <c>ForbiddenDuringImpersonation</c>.
/// </summary>
public static class ImpersonationRules
{
    public const string ForbiddenCode = "auth.impersonation_forbidden";

    public static Error Forbidden() =>
        Error.Forbidden(ForbiddenCode, "You are signed in as someone else. Confirming who you are, signing, approving, and changing their sign-in, factors, password or sessions are for them alone. End the impersonation to act as yourself.");

    /// <summary>The refusal when someone is signed in as the principal; success otherwise.</summary>
    public static Result Require(ICurrentPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.ImpersonatorId is null ? Result.Success() : Forbidden();
    }
}
