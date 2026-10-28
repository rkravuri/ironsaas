namespace Iron.Kernel;

/// <summary>
/// The kind of an expected failure. The API layer maps it to an HTTP status (T5):
/// Validation 400, Unauthorized 401, Forbidden 403, NotFound 404, Conflict 409, Unexpected 500, Unavailable 503,
/// Held 202, and TooManyRequests 429.
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

    /// <summary>The operation is stopped for now, on purpose (a kill switch, ADR-0035): the caller may try again later.</summary>
    Unavailable,

    /// <summary>
    /// Nothing applied yet, on purpose: the change waits for a second administrator's approval (TF-068, ADR-0052). Not a
    /// failure of the request, so the API answers 202 with the pending change's id; the change applies when approved.
    /// </summary>
    Held,

    /// <summary>Too many requests for a rate limit (TF-072, ADR-0053): the caller waits the time the answer names, then tries again.</summary>
    TooManyRequests,
}
