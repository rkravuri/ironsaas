using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Tenancy.Data;

/// <summary>
/// One change of a flag's rules (TF-053 AC1, TF-051 AC3): the rules before and after (JSON lists), the revision of the
/// flag's rules it made, whether it flipped a kill switch, the operator's reason, and who and when (the created pair).
/// Append-only: the app role may only insert and select, and the save interceptor refuses a change.
/// </summary>
public sealed class FlagRuleChange : AppendOnlyEntity, ITenantWide
{
    public const string RulesKind = "rules";
    public const string KillSwitchKind = "kill_switch";

    private FlagRuleChange()
    {
        FlagKey = string.Empty;
        Kind = string.Empty;
        Before = "[]";
        After = "[]";
    }

    public FlagRuleChange(string flagKey, string kind, int revision, string before, string after, string? reason)
        : this()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flagKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        FlagKey = flagKey;
        Kind = kind;
        Revision = revision;
        Before = before;
        After = after;
        Reason = reason is null ? null : reason[..Math.Min(reason.Length, FlagRule.ReasonMaxLength)];
    }

    public string FlagKey { get; private set; }

    /// <summary><c>rules</c>, or <c>kill_switch</c> for a flip through the kill-switch endpoint.</summary>
    public string Kind { get; private set; }

    /// <summary>The revision of the flag's rules after the change: 1 for the first.</summary>
    public int Revision { get; private set; }

    /// <summary>The flag's rules before (jsonb).</summary>
    public string Before { get; private set; }

    /// <summary>The flag's rules after (jsonb).</summary>
    public string After { get; private set; }

    public string? Reason { get; private set; }
}
