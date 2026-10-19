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
    DateTimeOffset? SessionStartedAt = null);

/// <summary>
/// The current principal (TF-010): the signed-in principal of the request, nobody when the request has no valid session,
/// and the system principal outside a request (jobs, commands). Registered by the module; it replaces the default
/// system principal of AddIronDatabase.
/// </summary>
public sealed class RequestPrincipal(IHttpContextAccessor accessor) : ICurrentPrincipal
{
    public const string ItemKey = "Iron.SignedInPrincipal";

    public Guid Id => Current?.Id ?? (accessor.HttpContext is null ? SystemPrincipal.SystemId : Guid.Empty);

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
