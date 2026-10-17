namespace Iron.Core.Results;

/// <summary>
/// Error codes are stable strings that clients and tests can rely on. Rule: lowercase segments separated by dots,
/// at least two segments, letters, digits and underscores only, each segment starting with a letter.
/// Examples: identity.login.invalid_credentials, authz.permission_denied, tenancy.hostname.taken.
/// </summary>
public static class ErrorCodes
{
    public static bool IsValid(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return false;
        }

        var segments = code.Split('.');
        if (segments.Length < 2)
        {
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment.Length == 0 || !char.IsAsciiLetterLower(segment[0]))
            {
                return false;
            }

            foreach (var character in segment)
            {
                if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != '_')
                {
                    return false;
                }
            }
        }

        return true;
    }
}
