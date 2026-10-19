namespace Iron.Kernel.Api.Modules;

/// <summary>A setting's definition and the module that declares it.</summary>
public sealed record DeclaredSetting(SettingDefinition Definition, string Module);

/// <summary>
/// Every setting the host's modules declare (ADR-0036), by key. AddModules builds it; the settings service answers only
/// for keys in it, so a value stored for a key no module declares any more is ignored, not served.
/// </summary>
public sealed class SettingDeclarations
{
    private readonly Dictionary<string, DeclaredSetting> _byKey;

    public SettingDeclarations(IEnumerable<IModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        _byKey = new Dictionary<string, DeclaredSetting>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            foreach (var setting in module.Settings)
            {
                if (!_byKey.TryAdd(setting.Key, new DeclaredSetting(setting, module.Name)))
                {
                    throw new InvalidOperationException($"The setting '{setting.Key}' is declared by the modules '{_byKey[setting.Key].Module}' and '{module.Name}'. A key has one owner.");
                }
            }
        }

        All = [.. _byKey.Values.OrderBy(s => s.Module, StringComparer.Ordinal).ThenBy(s => s.Definition.Key, StringComparer.Ordinal)];
    }

    /// <summary>By module, then by key.</summary>
    public IReadOnlyList<DeclaredSetting> All { get; }

    public DeclaredSetting? Find(string key) => _byKey.GetValueOrDefault(key);
}
