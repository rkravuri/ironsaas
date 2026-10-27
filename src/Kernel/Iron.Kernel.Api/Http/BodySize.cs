using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;

namespace Iron.Kernel.Api.Http;

/// <summary>
/// The largest body an endpoint accepts. Routing hands it to the server, which refuses a larger body before the handler
/// reads it; the request limit check asks it of every endpoint that reads its body itself (TF-071 AC1, ADR-0053).
/// </summary>
public sealed record BodySizeMetadata(long MaximumBytes) : IRequestSizeLimitMetadata
{
    long? IRequestSizeLimitMetadata.MaxRequestBodySize => MaximumBytes;
}

public static class BodySizeExtensions
{
    /// <summary>The endpoint accepts a body of at most <paramref name="maximumBytes"/> bytes.</summary>
    public static TBuilder LimitBodySize<TBuilder>(this TBuilder builder, long maximumBytes)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        return builder.WithMetadata(new BodySizeMetadata(maximumBytes));
    }
}
