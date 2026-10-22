using Iron.Kernel.Identity.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Identity.Services;

/// <summary>A person's link to a provider, as the user record shows it.</summary>
public sealed record SsoLinkView(Guid ProviderId, string ProviderName, string Email, DateTimeOffset LinkedAt, DateTimeOffset LastSignInAt);

/// <summary>Whether a person signs in only through a provider, why, and the provider to send them to.</summary>
public sealed record SsoOnlyStatus(bool SsoOnly, bool Marked, SsoProvider? Provider)
{
    public static readonly SsoOnlyStatus No = new(false, false, null);
}

/// <summary>
/// People and their providers (TF-014 AC2, ADR-0048): a person signs in only through a provider when an administrator
/// marked them so, or when they are linked to a provider whose people all sign in through it. Then a password sign-in is
/// refused with "Use your company login", and recovery links do not apply.
/// </summary>
public sealed class SsoPeople(IdentityDbContext context)
{
    public async Task<SsoOnlyStatus> SsoOnlyAsync(Principal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Type != PrincipalType.User)
        {
            return SsoOnlyStatus.No;
        }

        var linked = await LinkedProvidersAsync(principal.Id, cancellationToken).ConfigureAwait(false);
        if (principal.SsoOnly)
        {
            return new SsoOnlyStatus(true, true, linked.FirstOrDefault() ?? await ForEmailAsync(principal.Email ?? principal.LoginId, cancellationToken).ConfigureAwait(false));
        }

        return linked.FirstOrDefault(p => p.SsoOnly) is { } provider ? new SsoOnlyStatus(true, false, provider) : SsoOnlyStatus.No;
    }

    /// <summary>The provider whose allowed domains hold the email's domain, the one a person typing it is sent to; a domain belongs to one provider.</summary>
    public async Task<SsoProvider?> ForEmailAsync(string? email, CancellationToken cancellationToken)
    {
        var domain = SsoProvider.DomainOf(email);
        if (domain is null)
        {
            return null;
        }

        return await context.SsoProviders.AsNoTracking().FirstOrDefaultAsync(p => p.AllowedDomains.Contains(domain), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SsoProvider>> LinkedProvidersAsync(Guid principalId, CancellationToken cancellationToken) =>
        await context.SsoLinks.AsNoTracking()
            .Where(l => l.PrincipalId == principalId)
            .OrderBy(l => l.CreatedAt)
            .Join(context.SsoProviders.AsNoTracking(), l => l.ProviderId, p => p.Id, (l, p) => p)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<SsoLinkView>> LinksAsync(Guid principalId, CancellationToken cancellationToken) =>
        await context.SsoLinks.AsNoTracking()
            .Where(l => l.PrincipalId == principalId)
            .OrderBy(l => l.CreatedAt)
            .Join(context.SsoProviders.AsNoTracking(), l => l.ProviderId, p => p.Id, (l, p) => new SsoLinkView(p.Id, p.Name, l.Email, l.CreatedAt, l.LastSignInAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
