namespace Iron.Core.Results;

/// <summary>
/// The kind of an expected failure. The API layer maps it to an HTTP status (T5):
/// Validation 400, Unauthorized 401, Forbidden 403, NotFound 404, Conflict 409, Unexpected 500.
/// </summary>
public enum ErrorKind
{
    /// <summary>The request is not valid: missing or wrong fields, a broken business rule.</summary>
    Validation,

    /// <summary>The caller is not authenticated.</summary>
    Unauthorized,

    /// <summary>The caller is authenticated but may not do this.</summary>
    Forbidden,

    /// <summary>The record does not exist, or the caller may not know that it exists.</summary>
    NotFound,

    /// <summary>The change clashes with the current state: duplicate key, stale version, wrong status.</summary>
    Conflict,

    /// <summary>Something failed that should not fail. Logged with its correlation id; the caller sees no details.</summary>
    Unexpected,
}
