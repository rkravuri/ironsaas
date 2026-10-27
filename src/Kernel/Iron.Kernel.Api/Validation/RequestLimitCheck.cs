using System.Collections;
using System.IO.Pipelines;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentValidation;
using FluentValidation.Internal;
using FluentValidation.Validators;
using Iron.Kernel.Api.Permissions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Api.Validation;

/// <summary>
/// The start-up check of TF-071 AC1 (ADR-0053): every field of every request body has a declared limit. A string has a
/// maximum length, a collection a maximum count (and its items their own limits), a free JSON value a maximum size, and
/// an object a validator of its own whose fields pass the same rules; numbers, dates, identifiers and enumerations are
/// limited by their type. Rich text is a string here: its rule declares a maximum length. Every endpoint that takes a
/// body validates it with <c>.Validate&lt;T&gt;()</c>, unless its fields are all limited by their types; a body the
/// handler reads itself declares a request size limit. The host does not start otherwise, and the host tests start every
/// host, so a field without a limit fails the build. Registered by AddIronApi beside the permission check.
/// </summary>
public sealed class RequestLimitCheck(ILogger<RequestLimitCheck> logger) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return app =>
        {
            next(app);
            Run(app.ApplicationServices);
        };
    }

    public void Run(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        using var scope = services.CreateScope();
        var endpoints = services.GetRequiredService<EndpointDataSource>().Endpoints;
        var violations = Check(endpoints, type => (IValidator?)scope.ServiceProvider.GetService(typeof(IValidator<>).MakeGenericType(type)));
        if (violations.Count > 0)
        {
            throw new InvalidOperationException("The start-up check of request limits failed (TF-071):" + Environment.NewLine + string.Join(Environment.NewLine, violations));
        }

        logger.LogInformation("Request limit check passed: {Bodies} endpoint(s) take a body, each field limited.", endpoints.Count(e => BodyType(e) is not null));
    }

    /// <summary>The pure rules over the endpoints, unit tested without a host.</summary>
    public static IReadOnlyList<string> Check(IEnumerable<Endpoint> endpoints, Func<Type, IValidator?> validators)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(validators);
        var violations = new List<string>();
        var checkedTypes = new HashSet<Type>();
        foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
        {
            var body = BodyType(endpoint);
            if (body is null)
            {
                continue;
            }

            var name = EndpointPermissionCheck.Describe(endpoint);
            if (IsRawBody(body))
            {
                if (endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize is not > 0)
                {
                    violations.Add($"{name}: reads its body itself without a declared size. Add .WithMetadata(new RequestSizeLimitAttribute(bytes)).");
                }

                continue;
            }

            if (endpoint.Metadata.GetOrderedMetadata<ValidatedRequestMetadata>().All(validated => validated.RequestType != body))
            {
                // A body of flags, identifiers and dates is limited by its types alone; any other needs its validator.
                if (Check(body, null, validators).Count > 0)
                {
                    violations.Add($"{name}: takes {body.Name} without validating it. Add .Validate<{body.Name}>() and a validator with a limit for each field.");
                }

                continue;
            }

            if (checkedTypes.Add(body))
            {
                var validator = validators(body);
                if (validator is null)
                {
                    violations.Add($"{name}: no IValidator<{body.Name}> is registered.");
                    continue;
                }

                violations.AddRange(Check(body, validator, validators).Select(problem => $"{name}: {body.Name}.{problem}"));
            }
        }

        return violations;
    }

    /// <summary>The fields of one request type without a declared limit, by their path in the body (<c>items[].name</c>).</summary>
    public static IReadOnlyList<string> Check(Type requestType, IValidator? validator, Func<Type, IValidator?> validators)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentNullException.ThrowIfNull(validators);
        var problems = new List<string>();
        Walk(requestType, validator, string.Empty, validators, problems, []);
        return problems;
    }

    private static void Walk(Type type, IValidator? validator, string path, Func<Type, IValidator?> validators, List<string> problems, HashSet<Type> visiting)
    {
        if (!visiting.Add(type))
        {
            return; // a type that holds itself is checked once, where it first appears
        }

        var descriptor = validator?.CreateDescriptor();
        foreach (var property in BoundProperties(type))
        {
            var rules = descriptor?.GetRulesForMember(property.Name).ToList() ?? [];
            var own = rules.Where(rule => !IsCollectionRule(rule)).SelectMany(rule => rule.Components).Select(component => component.Validator).ToList();
            var each = rules.Where(IsCollectionRule).SelectMany(rule => rule.Components).Select(component => component.Validator).ToList();
            var field = path + JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name);
            Value(property.PropertyType, own, each, field, validators, problems, visiting);
        }

        visiting.Remove(type);
    }

    private static void Value(Type declared, List<IPropertyValidator> own, List<IPropertyValidator> each, string field, Func<Type, IValidator?> validators, List<string> problems, HashSet<Type> visiting)
    {
        var type = Nullable.GetUnderlyingType(declared) ?? declared;
        if (IsBoundedByType(type))
        {
            return;
        }

        // A string, or a value JSON reads from one (rich text): its rule declares a maximum length.
        if (type == typeof(string) || type.GetCustomAttribute<JsonConverterAttribute>() is not null)
        {
            if (!own.OfType<ILengthValidator>().Any(length => length.Max > 0))
            {
                problems.Add($"{field} is {(type == typeof(string) ? "a string" : $"{type.Name} text")} without a maximum length. Add .MaximumLength(n).");
            }

            return;
        }

        if (IsJson(type))
        {
            if (!Has(own, typeof(MaximumJsonSizeValidator<,>)))
            {
                problems.Add($"{field} is a JSON value without a maximum size. Add .MaximumJsonSize(bytes).");
            }

            return;
        }

        if (ElementType(type) is { } element)
        {
            if (!Has(own, typeof(MaximumCountValidator<,>)))
            {
                problems.Add($"{field} is a list without a maximum count. Add .MaximumCount(n).");
            }

            Value(element, each, [], field + "[]", validators, problems, visiting);
            return;
        }

        var child = own.OfType<IChildValidatorAdaptor>().FirstOrDefault();
        var childValidator = child is null ? null : ChildValidator(child, type, validators);
        if (childValidator is null)
        {
            problems.Add($"{field} is an object without a validator of its own. Add .SetValidator(...) with a limit for each of its fields.");
            return;
        }

        Walk(type, childValidator, field + ".", validators, problems, visiting);
    }

    /// <summary>The body's type as minimal APIs bind it, or null when the endpoint takes no body or a form's files.</summary>
    private static Type? BodyType(Endpoint endpoint)
    {
        var accepts = endpoint.Metadata.GetMetadata<IAcceptsMetadata>();
        var type = accepts?.RequestType;
        if (type is null || type == typeof(IFormFile) || type == typeof(IFormFileCollection) || type == typeof(IFormCollection))
        {
            return null;
        }

        return type;
    }

    private static bool IsRawBody(Type type) =>
        IsJson(type) || type == typeof(Stream) || type == typeof(PipeReader) || type == typeof(byte[]);

    private static bool IsJson(Type type) =>
        type == typeof(JsonElement) || type == typeof(JsonDocument) || typeof(JsonNode).IsAssignableFrom(type) || type == typeof(object);

    private static bool IsBoundedByType(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(decimal) || type == typeof(Guid) || type == typeof(DateTimeOffset)
        || type == typeof(DateTime) || type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(TimeSpan);

    private static Type? ElementType(Type type)
    {
        if (type == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(type))
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetElementType();
        }

        var enumerable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? type
            : type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0] ?? typeof(object);
    }

    private static bool Has(IEnumerable<IPropertyValidator> validators, Type definition) =>
        validators.Any(v => v.GetType().IsGenericType && v.GetType().GetGenericTypeDefinition() == definition);

    private static bool IsCollectionRule(IValidationRule rule) =>
        rule.GetType().GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICollectionRule<,>));

    /// <summary>The properties JSON binds: public, readable, and settable or a constructor's parameter (a record's).</summary>
    private static IEnumerable<PropertyInfo> BoundProperties(Type type)
    {
        var parameters = type.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .Where(p => p.SetMethod is { IsPublic: true } || parameters.Contains(p.Name))
            .Where(p => p.Name != "EqualityContract");
    }

    /// <summary>
    /// The validator a rule hands a nested object to: the one <c>SetValidator</c> or <c>ChildRules</c> was given, asked for
    /// with an empty context of the parent's type (ChildRules needs one), else the registered one.
    /// </summary>
    private static IValidator? ChildValidator(IChildValidatorAdaptor adaptor, Type type, Func<Type, IValidator?> validators)
    {
        var getter = adaptor.GetType().GetMethod("GetValidator");
        if (getter?.GetParameters() is [{ ParameterType: var contextType }, _])
        {
            try
            {
                var context = Activator.CreateInstance(contextType, [null]);
                if (getter.Invoke(adaptor, [context, null]) is IValidator given)
                {
                    return given;
                }
            }
            catch (Exception exception) when (exception is TargetInvocationException or MissingMethodException or ArgumentException)
            {
                // A validator chosen per value cannot be known before a request; the registered one stands for it.
            }
        }

        return validators(type);
    }
}
