namespace Iron.Kernel;

/// <summary>
/// One problem with one field of a request, for example ("email", "required", "The email is required.").
/// The field name is the JSON name the client sent (snake_case), so the UI can show the message next to the field.
/// </summary>
public sealed record FieldError(string Field, string Code, string Message);
