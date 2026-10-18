namespace Iron.Core.Principals;

/// <summary>Nobody is signed in. Identity (T10) registers this for a request without a session; the permission filter turns it into 401.</summary>
public sealed class AnonymousPrincipal : ICurrentPrincipal
{
    public static readonly AnonymousPrincipal Instance = new();

    private AnonymousPrincipal()
    {
    }

    public Guid Id => Guid.Empty;
}

public static class PrincipalExtensions
{
    public static bool IsAnonymous(this ICurrentPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.Id == Guid.Empty;
    }
}
