using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Flags.Contracts;
using Microsoft.Extensions.Logging;
using OpenFeature;
using OpenFeature.Model;

namespace Iron.Kernel.Flags.Services;

/// <summary>
/// The configured provider bound to OpenFeature (TF-056). Each host gets a domain of its own, so the hosts of a test run do
/// not share a provider through OpenFeature's process-wide API. A provider that fails to bind stops the host at start;
/// a misconfigured one never gets this far (FlagsModule refuses it).
/// </summary>
public sealed class OpenFeatureBinding
{
    public OpenFeatureBinding(FeatureProvider provider, ILogger<OpenFeatureBinding> logger)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(logger);
        Domain = $"iron-{Guid.NewGuid():N}";
        Provider = provider;
        OpenFeature.Api.Instance.SetProviderAsync(Domain, provider).GetAwaiter().GetResult();

        logger.LogInformation("Flags are evaluated through OpenFeature with the provider {Provider}.", provider.GetMetadata()?.Name);
    }

    public string Domain { get; }

    public FeatureProvider Provider { get; }

    public IFeatureClient Client(EvaluationContext context) => OpenFeature.Api.Instance.GetClient(Domain, null, null, context);
}

/// <summary>
/// Who a request evaluates flags for (ADR-0035): the unit's tenant; its region and plan from the tenant's rules
/// document; the principal's type and home unit from Identity through Authorization's contracts; the units above the
/// home unit from the tree. Built once per scope and principal.
/// </summary>
public sealed class FlagSubjects(FlagRulesCache cache, TenantContext? tenant = null, IPrincipals? principals = null, IOrganisationUnits? units = null)
{
    private readonly Dictionary<Guid, FlagSubject> _built = [];

    public async Task<FlagSubject> ForAsync(Guid principalId, CancellationToken cancellationToken)
    {
        if (_built.TryGetValue(principalId, out var known))
        {
            return known;
        }

        var rules = cache.Current?.Rules;
        string? type = null;
        IReadOnlyList<Guid> path = [];
        if (principalId != Guid.Empty && principals is not null)
        {
            var found = await principals.FindAsync([principalId], cancellationToken).ConfigureAwait(false);
            if (found.TryGetValue(principalId, out var principal))
            {
                type = principal.Type == PrincipalKind.Service ? "service" : "user";
                if (principal.HomeUnitId is { } home && units is not null)
                {
                    path = [.. (await units.PathAsync(home, cancellationToken).ConfigureAwait(false)).Reverse()];
                }
            }
        }

        var subject = new FlagSubject(
            tenant?.Id.Value ?? "local",
            rules?.Attribute(FlagAttributes.Region),
            rules?.Attribute(FlagAttributes.Plan),
            principalId == Guid.Empty ? null : principalId,
            type,
            path);
        _built[principalId] = subject;
        return subject;
    }

    /// <summary>The subject as OpenFeature's evaluation context: the principal's id is the targeting key.</summary>
    public static EvaluationContext ContextOf(FlagSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var builder = EvaluationContext.Builder().Set(FlagContextKeys.Tenant, subject.Tenant);
        if (subject.PrincipalId is { } principal)
        {
            builder.SetTargetingKey(principal.ToString("D"));
        }

        if (subject.Region is not null)
        {
            builder.Set(FlagContextKeys.Region, subject.Region);
        }

        if (subject.Plan is not null)
        {
            builder.Set(FlagContextKeys.Plan, subject.Plan);
        }

        if (subject.PrincipalType is not null)
        {
            builder.Set(FlagContextKeys.PrincipalType, subject.PrincipalType);
        }

        if (subject.Units.Count > 0)
        {
            builder.Set(FlagContextKeys.HomeUnit, subject.Units[0].ToString("D"));
            builder.Set(FlagContextKeys.Units, new Value(subject.Units.Select(u => new Value(u.ToString("D"))).ToList()));
        }

        return builder.Build();
    }
}

/// <summary>OpenFeature clients with the request's evaluation context, for product code (<see cref="IFlagClients"/>).</summary>
public sealed class FlagClients(OpenFeatureBinding binding, FlagSubjects subjects, ICurrentPrincipal principal) : IFlagClients
{
    public Task<IFeatureClient> CurrentAsync(CancellationToken cancellationToken) => ForAsync(principal.Id, cancellationToken);

    public async Task<IFeatureClient> ForAsync(Guid principalId, CancellationToken cancellationToken)
    {
        var subject = await subjects.ForAsync(principalId, cancellationToken).ConfigureAwait(false);
        return binding.Client(FlagSubjects.ContextOf(subject));
    }
}
