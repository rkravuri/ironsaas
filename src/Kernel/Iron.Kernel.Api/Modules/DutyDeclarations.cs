namespace Iron.Kernel.Api.Modules;

/// <summary>A duty and the module that declares it (<c>kernel</c> for <c>all</c>).</summary>
public sealed record DeclaredDuty(Duty Duty, string Module);

/// <summary>
/// Every duty the host's modules declare, with the kernel's <c>all</c> (ADR-0037), by code. AddModules builds it; a
/// delegation names only these, and acting for someone resolves their permissions from here.
/// </summary>
public sealed class DutyDeclarations
{
    private readonly Dictionary<string, DeclaredDuty> _byCode;

    public DutyDeclarations(IEnumerable<IModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        _byCode = new Dictionary<string, DeclaredDuty>(StringComparer.Ordinal)
        {
            [KernelDuties.AllCode] = new DeclaredDuty(KernelDuties.All, LicensingCatalogue.KernelModule),
        };
        foreach (var module in modules)
        {
            foreach (var duty in module.Duties)
            {
                if (duty.IsAll)
                {
                    throw new InvalidOperationException($"The module '{module.Name}' declares the duty 'all', which is the kernel's.");
                }

                if (!_byCode.TryAdd(duty.Code, new DeclaredDuty(duty, module.Name)))
                {
                    throw new InvalidOperationException($"The duty '{duty.Code}' is declared by the modules '{_byCode[duty.Code].Module}' and '{module.Name}'. A duty has one owner.");
                }
            }
        }

        All = [.. _byCode.Values.OrderBy(d => d.Duty.IsAll ? 0 : 1).ThenBy(d => d.Duty.Code, StringComparer.Ordinal)];
    }

    /// <summary><c>all</c> first, then by code.</summary>
    public IReadOnlyList<DeclaredDuty> All { get; }

    public DeclaredDuty? Find(string code) => _byCode.GetValueOrDefault(code);

    /// <summary>
    /// The permissions a set of duties covers, or null when one of them is <c>all</c>: no limit beyond what the delegator
    /// holds. A code no module declares any more covers nothing.
    /// </summary>
    public IReadOnlySet<string>? PermissionsOf(IEnumerable<string> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);
        var permissions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var code in codes)
        {
            if (Find(code) is not { } declared)
            {
                continue;
            }

            if (declared.Duty.IsAll)
            {
                return null;
            }

            permissions.UnionWith(declared.Duty.Permissions.Select(p => p.Name));
        }

        return permissions;
    }
}
