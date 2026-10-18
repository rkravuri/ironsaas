using System.Diagnostics;

namespace Iron.Core.Correlation;

/// <summary>
/// The correlation id of a request, job or event (TF-130). It is the W3C trace id of the current
/// <see cref="Activity"/>, so one id joins the API response header, the logs, the trace and the error page.
/// 32 lowercase hexadecimal characters.
/// </summary>
public readonly record struct CorrelationId
{
    public const int Length = 32;

    /// <summary>The response header that carries the id back to the client (TF-130).</summary>
    public const string HeaderName = "X-Correlation-Id";

    private readonly string? _value;

    private CorrelationId(string value)
    {
        _value = value;
    }

    public string Value => _value ?? string.Empty;

    public bool IsEmpty => _value is null;

    /// <summary>The id of the current trace, or a new random id when no trace is active (for example in a test).</summary>
    public static CorrelationId Current()
    {
        var activity = Activity.Current;
        return activity is null ? New() : new CorrelationId(activity.TraceId.ToHexString());
    }

    public static CorrelationId New() => new(ActivityTraceId.CreateRandom().ToHexString());

    public static bool TryParse(string? value, out CorrelationId id)
    {
        id = default;
        if (value is null || value.Length != Length)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        id = new CorrelationId(value.ToLowerInvariant());
        return true;
    }

    public override string ToString() => Value;
}
