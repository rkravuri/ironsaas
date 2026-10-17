using FluentValidation;
using Iron.Api.Responses;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Api.Validation;

/// <summary>
/// Validates the request body before the handler runs (TF-100: nothing is processed when the body is not valid).
/// Attached with <c>.Validate&lt;TRequest&gt;()</c>; the validator comes from dependency injection and must exist.
/// </summary>
public sealed class ValidationFilter<TRequest> : IEndpointFilter
    where TRequest : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var request = context.Arguments.OfType<TRequest>().FirstOrDefault();
        if (request is null)
        {
            return await next(context);
        }

        var validator = context.HttpContext.RequestServices.GetService<IValidator<TRequest>>()
            ?? throw new InvalidOperationException(
                $"No IValidator<{typeof(TRequest).Name}> is registered. Register the validator in the module's AddServices.");

        var result = await validator.ValidateAsync(request, context.HttpContext.RequestAborted);
        return result.IsValid
            ? await next(context)
            : ApiResults.Problem(FieldErrorMapper.ToError(result), context.HttpContext);
    }
}

public static class ValidationExtensions
{
    /// <summary>Validates the <typeparamref name="TRequest"/> argument of the endpoint with its registered validator.</summary>
    public static RouteHandlerBuilder Validate<TRequest>(this RouteHandlerBuilder builder)
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddEndpointFilter<ValidationFilter<TRequest>>().ProducesValidationProblem();
    }
}
