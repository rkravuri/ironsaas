using Iron.Kernel.Identity.Data;
using Microsoft.AspNetCore.Http;

namespace Iron.Kernel.Identity.Services;

/// <summary>How the request was authenticated: the session cookie of the shell, or a bearer token of an API client.</summary>
public enum AuthenticationKind
{
    Session,
    Token,
}

/// <summary>
/// What the session or bearer middleware found for this request. For a token, SessionId is the token id, both expiries
/// are its expiry, and there is no session start.
/// </summary>
public sealed record SignedInPrincipal(
    Guid Id,
    string LoginId,
    string DisplayName,
    PrincipalType Type,
    Guid SessionId,
    DateTimeOffset SessionExpiresAt,
    DateTimeOffset IdleExpiresAt,
    AuthenticationKind Kind = AuthenticationKind.Session,
    Guid? HomeUnitId = null,
    DateTimeOffset? SessionStartedAt = null,
    Guid? ActingForId = null,
    Guid? ChosenWorkingUnitId = null,
    bool AllUnits = false)
{
    /// <summary>The unit the person works for: the one chosen, else the home unit; the home unit in the consolidated view.</summary>
    public Guid? WorkingUnitId => Kind == AuthenticationKind.Session ? (AllUnits ? HomeUnitId : ChosenWorkingUnitId ?? HomeUnitId) : null;

    /// <summary>
    /// When the principal last proved who they are (TF-248): a session's sign-in or latest re-authentication, a token's
    /// issue. Null when nothing says, which no high-risk action accepts.
    /// </summary>
    public DateTimeOffset? AuthenticatedAt { get; init; }
}

/// <summary>
/// Step-up for a request (TF-248, ADR-0047): the signed-in principal's last authentication must be within the age the
/// action asks. Outside a request (a job, a command) nobody signs in, and the action goes on.
/// </summary>
public sealed class RequestRecentAuthentication(IHttpContextAccessor accessor, TimeProvider time) : IRecentAuthentication
{
    public Result Require(TimeSpan maxAge)
    {
        var http = accessor.HttpContext;
        if (http is null)
        {
            return Result.Success();
        }

        var current = http.Items.TryGetValue(RequestPrincipal.ItemKey, out var value) ? value as SignedInPrincipal : null;
        return current?.AuthenticatedAt is { } at && time.GetUtcNow() - at <= maxAge
            ? Result.Success()
            : RecentAuthentication.Required(maxAge);
    }
}

/// <summary>
/// The current principal (TF-010): the signed-in principal of the request, nobody when the request has no valid session,
/// and the system principal outside a request (jobs, commands). Registered by the module; it replaces the default
/// system principal of AddIronDatabase.
/// </summary>
public sealed class RequestPrincipal(IHttpContextAccessor accessor) : ICurrentPrincipal
{
    public const string ItemKey = "Iron.SignedInPrincipal";

    public Guid Id => Current?.Id ?? (accessor.HttpContext is null ? SystemPrincipal.SystemId : Guid.Empty);

    /// <summary>The delegator a shell session acts for (ADR-0037); never for a token.</summary>
    public Guid? ActingForId => Current is { Kind: AuthenticationKind.Session } signedIn ? signedIn.ActingForId : null;

    /// <summary>The unit a shell session works for (ADR-0038); never for a token.</summary>
    public Guid? WorkingUnitId => Current?.WorkingUnitId;

    /// <summary>The consolidated view of a shell session.</summary>
    public bool AllUnits => Current is { Kind: AuthenticationKind.Session, AllUnits: true };

    public SignedInPrincipal? Current
    {
        get
        {
            var context = accessor.HttpContext;
            return context is not null && context.Items.TryGetValue(ItemKey, out var value) ? value as SignedInPrincipal : null;
        }
    }

    public static void Set(HttpContext context, SignedInPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Items[ItemKey] = principal;
    }
}
