using System.Globalization;
using System.Text.Json;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Flags.Contracts;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Model;

namespace Iron.Kernel.Flags.Services;

/// <summary>
/// The in-house OpenFeature provider (TF-050, TF-056, ADR-0035): it answers from the rules the unit holds and the flags the
/// host's modules declare. An undeclared flag answers FLAG_NOT_FOUND with the caller's default; a type the declaration
/// does not have answers TYPE_MISMATCH. With rules known it evaluates them; with nothing known it answers the
/// declaration's default with the reason UNKNOWN, which the alert covers. A matched rule's id is the variant, and its
/// reason and time are in the metadata, for a kill switch's refusal and banner.
/// </summary>
public sealed class IronFlagProvider(FlagRulesCache cache, LicensingCatalogue catalogue) : FeatureProvider
{
    public const string Name = FlagsOptions.IronProvider;
    public const string RuleReasonKey = "reason";
    public const string RuleSinceKey = "since";
    public const string SourceKey = "source";

    private static readonly Metadata ProviderMetadata = new(Name);

    public override Metadata GetMetadata() => ProviderMetadata;

    public override Task<ResolutionDetails<bool>> ResolveBooleanValueAsync(string flagKey, bool defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Resolve(flagKey, defaultValue, context, FlagType.Boolean, value => value.GetBoolean()));

    public override Task<ResolutionDetails<string>> ResolveStringValueAsync(string flagKey, string defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Resolve(flagKey, defaultValue, context, FlagType.String, value => value.GetString() ?? string.Empty));

    public override Task<ResolutionDetails<int>> ResolveIntegerValueAsync(string flagKey, int defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Resolve(flagKey, defaultValue, context, FlagType.Number, value => (int)Math.Round(value.GetDouble(), MidpointRounding.AwayFromZero)));

    public override Task<ResolutionDetails<double>> ResolveDoubleValueAsync(string flagKey, double defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Resolve(flagKey, defaultValue, context, FlagType.Number, value => value.GetDouble()));

    public override Task<ResolutionDetails<Value>> ResolveStructureValueAsync(string flagKey, Value defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Resolve(flagKey, defaultValue, context, FlagType.Json, ToValue));

    /// <summary>The evaluation itself, without OpenFeature's types: the context part and the gate read the same answer.</summary>
    public FlagResult Evaluate(Flag flag, FlagSubject subject)
    {
        ArgumentNullException.ThrowIfNull(flag);
        var snapshot = cache.Current;
        return snapshot is null
            ? new FlagResult(flag.Default, FlagResult.UnknownReason, null)
            : FlagRules.Evaluate(flag, snapshot.Rules.Rules, subject);
    }

    /// <summary>The subject an evaluation context describes (the keys of <see cref="FlagContextKeys"/>).</summary>
    public static FlagSubject SubjectOf(EvaluationContext? context)
    {
        string? Text(string key) => context is not null && context.TryGetValue(key, out var value) && value is { IsString: true } ? value.AsString : null;
        var units = context is not null && context.TryGetValue(FlagContextKeys.Units, out var list) && list is { IsList: true, AsList: { } items }
            ? items.Where(v => v.IsString && Guid.TryParse(v.AsString, out _)).Select(v => Guid.Parse(v.AsString!, CultureInfo.InvariantCulture)).ToList()
            : [];
        var principal = Guid.TryParse(context?.TargetingKey, out var id) && id != Guid.Empty ? id : (Guid?)null;
        return new FlagSubject(Text(FlagContextKeys.Tenant) ?? string.Empty, Text(FlagContextKeys.Region), Text(FlagContextKeys.Plan), principal, Text(FlagContextKeys.PrincipalType), units);
    }

    /// <summary>The declaration of a flag the host knows, retired ones excluded.</summary>
    public Flag? Declaration(string key) => catalogue.FindActiveFlag(key) is { } entry
        ? new Flag(entry.Key, entry.Type, entry.Kind, entry.Purpose, entry.Owner, entry.RemoveBy, entry.Default)
        : null;

    /// <summary>A JSON value as OpenFeature's <see cref="Value"/>.</summary>
    public static Value ToValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => new Value(true),
        JsonValueKind.False => new Value(false),
        JsonValueKind.Number => new Value(element.GetDouble()),
        JsonValueKind.String => new Value(element.GetString()!),
        JsonValueKind.Array => new Value(element.EnumerateArray().Select(ToValue).ToList()),
        JsonValueKind.Object => new Value(new Structure(element.EnumerateObject().ToDictionary(p => p.Name, p => ToValue(p.Value)))),
        _ => new Value(),
    };

    private ResolutionDetails<T> Resolve<T>(string flagKey, T defaultValue, EvaluationContext? context, FlagType type, Func<JsonElement, T> convert)
    {
        var flag = Declaration(flagKey);
        if (flag is null)
        {
            return new ResolutionDetails<T>(flagKey, defaultValue, ErrorType.FlagNotFound, Reason.Error, null, $"No module of this host declares the flag '{flagKey}'.");
        }

        if (flag.Type != type)
        {
            return new ResolutionDetails<T>(flagKey, defaultValue, ErrorType.TypeMismatch, Reason.Error, null, $"The flag '{flagKey}' is a {flag.Type.ToString().ToLowerInvariant()} flag.");
        }

        var result = Evaluate(flag, SubjectOf(context));
        var metadata = new Dictionary<string, object>(StringComparer.Ordinal) { [SourceKey] = cache.Current is null ? "code_default" : "rules" };
        if (result.Rule is { } rule)
        {
            metadata[RuleSinceKey] = rule.ChangedAt.ToString("O", CultureInfo.InvariantCulture);
            if (rule.Reason is not null)
            {
                metadata[RuleReasonKey] = rule.Reason;
            }
        }

        var reason = result.Reason switch
        {
            FlagResult.TargetingReason => Reason.TargetingMatch,
            FlagResult.SplitReason => Reason.Split,
            FlagResult.UnknownReason => Reason.Unknown,
            _ => Reason.Default,
        };
        return new ResolutionDetails<T>(flagKey, convert(result.Value), ErrorType.None, reason, result.Rule?.Id.ToString("D") ?? "default", null, new ImmutableMetadata(metadata));
    }
}
