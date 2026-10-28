using System.Collections;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using FluentValidation.Validators;

namespace Iron.Kernel.Api.Validation;

/// <summary>Says which request type an endpoint validates, so the start-up check can find its validator (TF-071 AC1).</summary>
public sealed record ValidatedRequestMetadata(Type RequestType);

/// <summary>A limit FluentValidation has no built-in name for, so the start-up check can read it from the descriptor.</summary>
public interface IDeclaredLimit
{
    /// <summary>The limit in words, for messages: "at most 50 items".</summary>
    string Limit { get; }
}

/// <summary>A collection holds at most so many items (TF-071 AC1).</summary>
public sealed class MaximumCountValidator<T, TCollection>(int maximum) : PropertyValidator<T, TCollection>, IDeclaredLimit
    where TCollection : IEnumerable?
{
    public int Maximum { get; } = maximum;

    public override string Name => "MaximumCountValidator";

    public string Limit => $"at most {Maximum} items";

    public override bool IsValid(ValidationContext<T> context, TCollection value)
    {
        ArgumentNullException.ThrowIfNull(context);
        var count = value switch
        {
            null => 0,
            ICollection collection => collection.Count,
            _ => value.Cast<object?>().Count(),
        };
        if (count <= Maximum)
        {
            return true;
        }

        context.MessageFormatter.AppendArgument("MaximumCount", Maximum).AppendArgument("TotalCount", count);
        return false;
    }

    protected override string GetDefaultMessageTemplate(string errorCode) =>
        "'{PropertyName}' must have {MaximumCount} items or fewer. You sent {TotalCount}.";
}

/// <summary>A free JSON value (a setting's value, a field's answer) is at most so many bytes once written (TF-071 AC1).</summary>
public sealed class MaximumJsonSizeValidator<T, TValue>(int maximumBytes) : PropertyValidator<T, TValue>, IDeclaredLimit
{
    public int MaximumBytes { get; } = maximumBytes;

    public override string Name => "MaximumJsonSizeValidator";

    public string Limit => $"at most {MaximumBytes} bytes";

    public override bool IsValid(ValidationContext<T> context, TValue value)
    {
        ArgumentNullException.ThrowIfNull(context);
        var size = value switch
        {
            null => 0,
            JsonElement { ValueKind: JsonValueKind.Undefined } => 0,
            JsonElement element => Encoding.UTF8.GetByteCount(element.GetRawText()),
            JsonNode node => Encoding.UTF8.GetByteCount(node.ToJsonString()),
            _ => JsonSerializer.SerializeToUtf8Bytes(value).Length,
        };
        if (size <= MaximumBytes)
        {
            return true;
        }

        context.MessageFormatter.AppendArgument("MaximumBytes", MaximumBytes);
        return false;
    }

    protected override string GetDefaultMessageTemplate(string errorCode) =>
        "'{PropertyName}' must be {MaximumBytes} bytes or fewer.";
}

public static class RequestLimitRules
{
    /// <summary>The collection holds at most <paramref name="maximum"/> items.</summary>
    public static IRuleBuilderOptions<T, TCollection> MaximumCount<T, TCollection>(this IRuleBuilder<T, TCollection> rule, int maximum)
        where TCollection : IEnumerable?
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        return rule.SetValidator(new MaximumCountValidator<T, TCollection>(maximum));
    }

    /// <summary>The JSON value is at most <paramref name="maximumBytes"/> bytes once written.</summary>
    public static IRuleBuilderOptions<T, TValue> MaximumJsonSize<T, TValue>(this IRuleBuilder<T, TValue> rule, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        return rule.SetValidator(new MaximumJsonSizeValidator<T, TValue>(maximumBytes));
    }
}

/// <summary>
/// The limits of fields many modules' requests share (TF-071 AC1, ADR-0053). A field stored in a column uses the
/// column's own limit; these are for what passes through: credentials, codes, and the answers of a passkey.
/// </summary>
public static class FieldLimits
{
    /// <summary>A login identifier: an e-mail address at most, as the principal's column holds it.</summary>
    public const int LoginId = 254;

    /// <summary>A password: long enough for a passphrase, short enough that hashing it costs nothing extra.</summary>
    public const int Password = 1024;

    /// <summary>A code from an authenticator app.</summary>
    public const int OneTimeCode = 20;

    /// <summary>A recovery code, with its separators.</summary>
    public const int RecoveryCode = 40;

    /// <summary>A passkey's answer from the browser: a WebAuthn assertion or attestation, as JSON.</summary>
    public const int PasskeyBytes = 16 * 1024;

    /// <summary>A path inside the shell to return to.</summary>
    public const int ReturnPath = 500;

    /// <summary>A permission's or a duty's name, such as <c>authz.roles.manage</c>.</summary>
    public const int PermissionName = 100;
}
