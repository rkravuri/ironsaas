namespace Iron.Kernel;

/// <summary>
/// An expected failure: what kind it is, a stable code, a message for people, and field problems when it is a
/// validation error. Services return errors inside a <see cref="Result"/>; exceptions are kept for programmer
/// errors and infrastructure failures.
/// </summary>
public sealed class Error
{
    public Error(ErrorKind kind, string code, string message, IReadOnlyList<FieldError>? fields = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!ErrorCodes.IsValid(code))
        {
            throw new ArgumentException(
                $"'{code}' is not a valid error code. Use lowercase dotted segments such as identity.login.invalid_credentials.",
                nameof(code));
        }

        Kind = kind;
        Code = code;
        Message = message;
        Fields = fields ?? [];
    }

    public ErrorKind Kind { get; }

    /// <summary>Stable, machine-readable code such as <c>identity.login.invalid_credentials</c>.</summary>
    public string Code { get; }

    /// <summary>A message for people. It may be shown to the user, so it must not leak internals.</summary>
    public string Message { get; }

    /// <summary>Field-level problems; empty unless the kind is Validation.</summary>
    public IReadOnlyList<FieldError> Fields { get; }

    /// <summary>
    /// Facts a client acts on, beyond the code: how many seconds to wait before trying again, for example. The problem
    /// carries each as an extension of its own name. Never a secret or anything about another person.
    /// </summary>
    public IReadOnlyDictionary<string, object> Extensions { get; init; } = new Dictionary<string, object>(StringComparer.Ordinal);

    public static Error Validation(string code, string message, params FieldError[] fields)
        => new(ErrorKind.Validation, code, message, fields);

    public static Error Validation(string code, string message, IReadOnlyList<FieldError> fields)
        => new(ErrorKind.Validation, code, message, fields);

    public static Error Unauthorized(string code, string message) => new(ErrorKind.Unauthorized, code, message);

    public static Error Forbidden(string code, string message) => new(ErrorKind.Forbidden, code, message);

    public static Error NotFound(string code, string message) => new(ErrorKind.NotFound, code, message);

    public static Error Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);

    public static Error Unexpected(string code, string message) => new(ErrorKind.Unexpected, code, message);

    public static Error Unavailable(string code, string message) => new(ErrorKind.Unavailable, code, message);

    /// <summary>The change waits for approval (TF-068): the extensions carry the pending change's id.</summary>
    public static Error Held(string code, string message) => new(ErrorKind.Held, code, message);

    /// <summary>Too many requests for a rate limit (TF-072): the extensions carry the seconds to wait.</summary>
    public static Error TooManyRequests(string code, string message) => new(ErrorKind.TooManyRequests, code, message);

    public override string ToString() => $"{Kind} {Code}: {Message}";
}
