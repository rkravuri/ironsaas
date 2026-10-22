using Iron.Kernel.Api.Responses;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Iron.Kernel.Api.Permissions;

/// <summary>
/// A step the signed-in principal must take before anything else, such as enrolling the second factor the tenant's policy
/// now requires of them (ADR-0047). The authentication middleware sets it; the permission and signed-in filters answer
/// its problem for every endpoint but those declared <see cref="RequiredStepEndpointExtensions.AllowDuringRequiredStep"/>,
/// which are the step's own (who am I, sign out, enrol, confirm it is me). The session stays: nobody is signed out.
/// </summary>
public static class RequiredStep
{
    public const string ItemKey = "Iron.RequiredStep";

    public static void Set(HttpContext context, Error step)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(step);
        context.Items[ItemKey] = step;
    }

    public static Error? Get(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(ItemKey, out var value) ? value as Error : null;
    }

    /// <summary>The step's problem, unless there is none or the endpoint is one of the step's own.</summary>
    public static IResult? Refusal(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Get(context) is { } step && context.GetEndpoint()?.Metadata.GetMetadata<AllowedDuringRequiredStepMetadata>() is null
            ? ApiResults.Problem(step, context)
            : null;
    }
}

/// <summary>Endpoint metadata: the endpoint answers while a required step is pending, since it is part of taking it.</summary>
public sealed class AllowedDuringRequiredStepMetadata;

public static class RequiredStepEndpointExtensions
{
    public static TBuilder AllowDuringRequiredStep<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(new AllowedDuringRequiredStepMetadata());
        return builder;
    }
}
