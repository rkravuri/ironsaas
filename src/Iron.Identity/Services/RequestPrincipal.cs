using Iron.Core.Principals;
using Iron.Identity.Data;
using Microsoft.AspNetCore.Http;

namespace Iron.Identity.Services;

/// <summary>What the session middleware found for this request.</summary>
public sealed record SignedInPrincipal(Guid Id, string LoginId, string DisplayName, PrincipalType Type, Guid SessionId, DateTimeOffset SessionExpiresAt, DateTimeOffset IdleExpiresAt);

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
