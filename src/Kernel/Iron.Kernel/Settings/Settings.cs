using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Iron.Kernel;

/// <summary>The value a setting holds (TF-057): text, a number, a boolean, one of a list, a JSON document, or a reference to a secret.</summary>
public enum SettingType
{
    Text,
    Number,
    Boolean,
    Choice,
    Json,
    SecretReference,
}

/// <summary>
/// Where a value may be set (TF-057, TF-221): for the whole tenant, for an organisation unit and the units below it, or
/// for one person. The default in code is the system level and needs no scope.
/// </summary>
[Flags]
public enum SettingScopes
{
    None = 0,
    Tenant = 1,
    Unit = 2,
    User = 4,
}

/// <summary>The level an effective value comes from: the person's own, a unit's, the tenant's, or the default in code.</summary>
public enum SettingLevel
{
    Default,
    Tenant,
    Unit,
    User,
}

/// <summary>
/// A path in the secret store that holds a setting's secret under the key <c>value</c> (TF-057 AC3, ADR-0036). The path is
/// what is stored, shown and sent; the secret is read from the store only by the code that uses it.
/// </summary>
public sealed partial record SecretReference
{
    public const string ValueKey = "value";

    public SecretReference(string path)
    {
        if (!IsValidPath(path))
        {
            throw new ArgumentException($"'{path}' is not a path in the secret store. Use lowercase segments separated by slashes, for example tenants/acme/settings/mail.", nameof(path));
        }

        Path = path;
    }

    public string Path { get; }

    public static bool IsValidPath(string? path) => path is not null && PathPattern().IsMatch(path);

    public override string ToString() => Path;

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*(/[a-z0-9][a-z0-9_-]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();
}

/// <summary>
/// A rule a value must keep, in the words the person who sets it reads when it is refused (TF-057 AC2). The bounds are
/// kept beside the words, so the shell's form can say the same before the value is sent.
/// </summary>
public sealed class SettingRule
{
    private readonly Func<JsonElement, bool> _holds;

    private SettingRule(string words, Func<JsonElement, bool> holds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(words);
        Words = words;
        _holds = holds;
    }

    /// <summary>The rule as a sentence: "A whole number from 15 to 480."</summary>
    public string Words { get; }

    public decimal? Minimum { get; private init; }

    public decimal? Maximum { get; private init; }

    public int? MinimumLength { get; private init; }

    public int? MaximumLength { get; private init; }

    public bool Holds(JsonElement value) => _holds(value);

    /// <summary>A number between two bounds, both included.</summary>
    public static SettingRule Range(decimal minimum, decimal maximum, bool wholeNumber)
    {
        if (minimum > maximum)
        {
            throw new ArgumentException($"The range {minimum} to {maximum} is empty.", nameof(maximum));
        }

        var words = string.Create(CultureInfo.InvariantCulture, $"{(wholeNumber ? "A whole number" : "A number")} from {minimum} to {maximum}.");
        return new SettingRule(words, value => value.TryGetDecimal(out var number) && number >= minimum && number <= maximum) { Minimum = minimum, Maximum = maximum };
    }

    /// <summary>A text of a length between two bounds, both included.</summary>
    public static SettingRule Length(int minimum, int maximum)
    {
        if (minimum < 0 || minimum > maximum)
        {
            throw new ArgumentException($"The length {minimum} to {maximum} is not a range.", nameof(maximum));
        }

        var words = string.Create(CultureInfo.InvariantCulture, $"From {minimum} to {maximum} characters.");
        return new SettingRule(words, value => value.GetString() is { } text && text.Length >= minimum && text.Length <= maximum) { MinimumLength = minimum, MaximumLength = maximum };
    }

    /// <summary>A text that matches a pattern; <paramref name="words"/> says what the pattern means, never the pattern itself.</summary>
    public static SettingRule Matches(string pattern, string words)
    {
        var regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return new SettingRule(words, value => value.GetString() is { } text && regex.IsMatch(text));
    }

    /// <summary>Any rule code can say: <paramref name="holds"/> decides, <paramref name="words"/> explains.</summary>
    public static SettingRule Must(Func<JsonElement, bool> holds, string words)
    {
        ArgumentNullException.ThrowIfNull(holds);
        return new SettingRule(words, holds);
    }
}

/// <summary>
/// A setting as its module declares it in code (TF-057, ADR-0036): key, type, default, the rule a value keeps, the scopes
/// a value may be set at, whether the shell may see it, and what it is for. The typed form, <see cref="Setting{T}"/>, is
/// also the accessor: code reads <c>settings.GetAsync(IdentitySettings.SessionIdleMinutes, context)</c>, so a key nobody
/// declared cannot be written (TF-059 AC1).
/// </summary>
public abstract partial class SettingDefinition
{
    protected SettingDefinition(string key, SettingType type, string description, SettingScopes scopes, bool visibleToShell, JsonElement defaultValue, SettingRule? rule, IReadOnlyList<string>? choices, bool wholeNumber)
    {
        if (!IsValidKey(key))
        {
            throw new ArgumentException($"'{key}' is not a setting key. Use dotted lowercase words, for example identity.session.idle_minutes.", nameof(key));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException($"The setting '{key}' declares no description.", nameof(description));
        }

        if (scopes == SettingScopes.None)
        {
            throw new ArgumentException($"The setting '{key}' allows no scope: name the tenant, the unit or the user, or all three.", nameof(scopes));
        }

        if (type == SettingType.SecretReference && visibleToShell)
        {
            throw new ArgumentException($"The setting '{key}' is a secret reference, and the shell never sees one (TF-057 AC3).", nameof(visibleToShell));
        }

        if (type == SettingType.Choice && (choices is null || choices.Count == 0))
        {
            throw new ArgumentException($"The choice setting '{key}' declares no choices.", nameof(choices));
        }

        Key = key;
        Type = type;
        Description = description.Trim();
        Scopes = scopes;
        VisibleToShell = visibleToShell;
        Rule = rule;
        Choices = choices ?? [];
        WholeNumber = wholeNumber;
        Default = defaultValue.Clone();

        // A secret reference has no value until one is set; every other default keeps its own rule.
        if (!(type == SettingType.SecretReference && defaultValue.ValueKind == JsonValueKind.Null) && Problem(defaultValue) is { } problem)
        {
            throw new ArgumentException($"The default of the setting '{key}' breaks its own rule: {problem}", nameof(defaultValue));
        }
    }

    public string Key { get; }

    public SettingType Type { get; }

    public string Description { get; }

    public SettingScopes Scopes { get; }

    /// <summary>Whether <c>/me/context</c> sends the effective value to the shell. Never for a secret reference.</summary>
    public bool VisibleToShell { get; }

    /// <summary>The value at the system level: what applies when nobody set one (ADR-0036).</summary>
    public JsonElement Default { get; }

    public SettingRule? Rule { get; }

    /// <summary>The values a choice setting allows, in the order the shell offers them.</summary>
    public IReadOnlyList<string> Choices { get; }

    /// <summary>A number setting that holds whole numbers only.</summary>
    public bool WholeNumber { get; }

    public bool IsSecret => Type == SettingType.SecretReference;

    /// <summary>
    /// A change of this setting is a high-risk action: setting or resetting it needs a recent authentication (TF-248,
    /// ADR-0047), as the MFA policy does. Declared with <see cref="Setting{T}.RequiringRecentAuthentication"/>.
    /// </summary>
    public bool NeedsRecentAuthentication { get; protected set; }

    /// <summary>What a value must be, in words: the type, the choices or the rule.</summary>
    public string Requirement => Type switch
    {
        SettingType.Choice => $"One of: {string.Join(", ", Choices)}.",
        _ when Rule is not null => Rule.Words,
        SettingType.Text => "Any text.",
        SettingType.Number => WholeNumber ? "A whole number." : "A number.",
        SettingType.Boolean => "On or off.",
        SettingType.Json => "A JSON document.",
        SettingType.SecretReference => "A path in the secret store, for example tenants/acme/settings/mail.",
        _ => "A value.",
    };

    public bool Allows(SettingLevel level) => level switch
    {
        SettingLevel.Default => true,
        SettingLevel.Tenant => Scopes.HasFlag(SettingScopes.Tenant),
        SettingLevel.Unit => Scopes.HasFlag(SettingScopes.Unit),
        SettingLevel.User => Scopes.HasFlag(SettingScopes.User),
        _ => false,
    };

    /// <summary>Null when the value may be saved; otherwise what the value must be, in words (TF-057 AC2).</summary>
    public string? Problem(JsonElement value)
    {
        var fits = Type switch
        {
            SettingType.Text => value.ValueKind == JsonValueKind.String,
            SettingType.Choice => value.ValueKind == JsonValueKind.String && Choices.Contains(value.GetString()!, StringComparer.Ordinal),
            SettingType.Number => value.ValueKind == JsonValueKind.Number && (!WholeNumber || (value.TryGetDecimal(out var number) && number == decimal.Truncate(number))),
            SettingType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            SettingType.Json => value.ValueKind is not JsonValueKind.Undefined,
            SettingType.SecretReference => value.ValueKind == JsonValueKind.String && SecretReference.IsValidPath(value.GetString()),
            _ => false,
        };

        return fits && (Rule is null || Rule.Holds(value)) ? null : Requirement;
    }

    public static bool IsValidKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    public override string ToString() => Key;

    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}

/// <summary>A setting with the type its value is read as. Build one with the factories of <see cref="Setting"/>.</summary>
public sealed class Setting<T> : SettingDefinition
{
    private readonly Func<JsonElement, T> _read;
    private readonly Func<T, JsonElement> _write;

    internal Setting(string key, SettingType type, string description, SettingScopes scopes, bool visibleToShell, T defaultValue, SettingRule? rule, IReadOnlyList<string>? choices, bool wholeNumber, Func<JsonElement, T> read, Func<T, JsonElement> write)
        : base(key, type, description, scopes, visibleToShell, write(defaultValue), rule, choices, wholeNumber)
    {
        _read = read;
        _write = write;
    }

    public T DefaultValue => _read(Default);

    /// <summary>A stored or default value, as the type the code reads.</summary>
    public T Read(JsonElement value) => _read(value);

    public JsonElement ToJson(T value) => _write(value);

    /// <summary>Marks a change of the setting as a high-risk action, behind a recent authentication (TF-248). Once, where it is declared.</summary>
    public Setting<T> RequiringRecentAuthentication()
    {
        NeedsRecentAuthentication = true;
        return this;
    }
}

/// <summary>The factories for settings, one per type (TF-057).</summary>
public static class Setting
{
    public static Setting<string> Text(string key, string description, string defaultValue, SettingScopes scopes, SettingRule? rule = null, bool visibleToShell = false) =>
        new(key, SettingType.Text, description, scopes, visibleToShell, defaultValue, rule, null, false, value => value.GetString() ?? string.Empty, value => JsonSerializer.SerializeToElement(value));

    /// <summary>A whole number between two bounds, both included.</summary>
    public static Setting<int> WholeNumber(string key, string description, int defaultValue, int minimum, int maximum, SettingScopes scopes, bool visibleToShell = false) =>
        new(key, SettingType.Number, description, scopes, visibleToShell, defaultValue, SettingRule.Range(minimum, maximum, wholeNumber: true), null, true, value => value.GetInt32(), value => JsonSerializer.SerializeToElement(value));

    /// <summary>
    /// A whole number that keeps a rule of its own, for values a range cannot say, such as "off, or from 30 to 365". The
    /// rule must refuse anything that is not a whole number.
    /// </summary>
    public static Setting<int> WholeNumber(string key, string description, int defaultValue, SettingRule rule, SettingScopes scopes, bool visibleToShell = false)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return new(key, SettingType.Number, description, scopes, visibleToShell, defaultValue, rule, null, true, value => value.GetInt32(), value => JsonSerializer.SerializeToElement(value));
    }

    /// <summary>A number between two bounds, both included.</summary>
    public static Setting<decimal> Number(string key, string description, decimal defaultValue, decimal minimum, decimal maximum, SettingScopes scopes, bool visibleToShell = false) =>
        new(key, SettingType.Number, description, scopes, visibleToShell, defaultValue, SettingRule.Range(minimum, maximum, wholeNumber: false), null, false, value => value.GetDecimal(), value => JsonSerializer.SerializeToElement(value));

    /// <summary>On or off; a <paramref name="rule"/> may hold it to one of them, as a control that must stay on.</summary>
    public static Setting<bool> Boolean(string key, string description, bool defaultValue, SettingScopes scopes, bool visibleToShell = false, SettingRule? rule = null) =>
        new(key, SettingType.Boolean, description, scopes, visibleToShell, defaultValue, rule, null, false, value => value.GetBoolean(), value => JsonSerializer.SerializeToElement(value));

    /// <summary>One of a list of words, offered in the list's order.</summary>
    public static Setting<string> Choice(string key, string description, string defaultValue, IReadOnlyList<string> choices, SettingScopes scopes, bool visibleToShell = false) =>
        new(key, SettingType.Choice, description, scopes, visibleToShell, defaultValue, null, choices, false, value => value.GetString() ?? string.Empty, value => JsonSerializer.SerializeToElement(value));

    public static Setting<JsonElement> Json(string key, string description, string defaultJson, SettingScopes scopes, SettingRule? rule = null, bool visibleToShell = false)
    {
        using var document = JsonDocument.Parse(defaultJson);
        return new(key, SettingType.Json, description, scopes, visibleToShell, document.RootElement.Clone(), rule, null, false, value => value.Clone(), value => value.Clone());
    }

    /// <summary>
    /// A reference to a secret in the store, with no value until one is set. The API and the shell see the path only;
    /// code reads the secret through <c>ISecretSettings</c>, which fetches it from the store (TF-059 AC3).
    /// </summary>
    public static Setting<SecretReference?> Secret(string key, string description, SettingScopes scopes) =>
        new(
            key,
            SettingType.SecretReference,
            description,
            scopes,
            false,
            null,
            null,
            null,
            false,
            value => value.ValueKind == JsonValueKind.String && value.GetString() is { } path ? new SecretReference(path) : null,
            value => value is null ? JsonSerializer.SerializeToElement<string?>(null) : JsonSerializer.SerializeToElement(value.Path));
}
