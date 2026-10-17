namespace Iron.Api.Problems;

/// <summary>The error codes the API layer itself produces. Module codes live in the modules.</summary>
public static class ApiErrors
{
    public const string Validation = "api.validation.failed";
    public const string ListInvalid = "api.list.invalid";
    public const string MalformedRequest = "api.request.malformed";
    public const string AuthenticationRequired = "api.auth.required";
    public const string Forbidden = "api.auth.forbidden";
    public const string RouteNotFound = "api.route.not_found";
    public const string MethodNotAllowed = "api.method.not_allowed";
    public const string Conflict = "api.request.conflict";
    public const string UnsupportedMediaType = "api.media_type.unsupported";
    public const string RateLimited = "api.rate_limited";
    public const string RequestFailed = "api.request.failed";
    public const string Unexpected = "platform.unexpected";

    /// <summary>The code for a problem the framework produced from a status code alone (a missing route, bad JSON).</summary>
    public static string ForStatus(int status)
    {
        return status switch
        {
            400 => MalformedRequest,
            401 => AuthenticationRequired,
            403 => Forbidden,
            404 => RouteNotFound,
            405 => MethodNotAllowed,
            409 => Conflict,
            415 => UnsupportedMediaType,
            429 => RateLimited,
            >= 500 => Unexpected,
            _ => RequestFailed,
        };
    }
}
