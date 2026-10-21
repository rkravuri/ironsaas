namespace Iron.Kernel;

/// <summary>
/// Step-up re-authentication (TF-248, ADR-0047): the one question every high-risk action asks before it changes anything,
/// whether the principal acting now proved who they are recently. Identity answers it for a person from their session's
/// last authentication and for a client from its token's issue; an endpoint asks through <c>RequireRecentAuthentication</c>,
/// a service that decides case by case (a role that holds a high-risk permission, a setting that guards sign-in) asks it
/// directly. The refusal is 403 <c>auth.reauthentication_required</c>, which the shell turns into its re-authentication
/// dialog before it sends the request again.
/// </summary>
public interface IRecentAuthentication
{
    /// <summary>Succeeds when the principal acting now authenticated within <paramref name="maxAge"/>; otherwise the refusal.</summary>
    Result Require(TimeSpan maxAge);
}

public static class RecentAuthentication
{
    public const string ErrorCode = "auth.reauthentication_required";

    /// <summary>How recent an authentication a high-risk action needs unless it says otherwise (TF-248).</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromMinutes(5);

    public static Error Required(TimeSpan maxAge) =>
        Error.Forbidden(ErrorCode, $"Confirm it is you to continue. This action needs you to have signed in within the last {Math.Max(1, (int)maxAge.TotalMinutes)} minutes.");

    /// <summary>The default age.</summary>
    public static Result Require(this IRecentAuthentication recent)
    {
        ArgumentNullException.ThrowIfNull(recent);
        return recent.Require(DefaultMaxAge);
    }
}

/// <summary>
/// The answer where nobody signs in: a job, a seed, a command, or a host without Identity. Identity replaces it with the
/// request's answer, so every request of a unit asks the session or the token.
/// </summary>
public sealed class NoSignInRecentAuthentication : IRecentAuthentication
{
    public static readonly NoSignInRecentAuthentication Instance = new();

    private NoSignInRecentAuthentication()
    {
    }

    public Result Require(TimeSpan maxAge) => Result.Success();
}
