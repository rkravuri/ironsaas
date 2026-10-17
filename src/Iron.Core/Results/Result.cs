namespace Iron.Core.Results;

/// <summary>
/// The outcome of an operation that has no value: success, or failure with an <see cref="Error"/>.
/// Read it with <c>if (result.IsFailure) return result.Error;</c>. No chaining helpers on purpose: plain code reads best.
/// </summary>
public class Result
{
    private readonly Error? _error;

    protected Result(Error? error)
    {
        _error = error;
    }

    public bool IsSuccess => _error is null;

    public bool IsFailure => _error is not null;

    /// <summary>The error of a failed result. Throws when the result is a success.</summary>
    public Error Error => _error ?? throw new InvalidOperationException("The result is a success and has no error.");

    public static Result Success() => new(null);

    public static Result Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(error);
    }

    public static Result<T> Success<T>(T value) => Result<T>.Success(value);

    public static Result<T> Failure<T>(Error error) => Result<T>.Failure(error);

    public static implicit operator Result(Error error) => Failure(error);

    public static Result FromError(Error error) => Failure(error);

    public override string ToString() => IsSuccess ? "Success" : $"Failure: {Error}";
}

/// <summary>
/// The outcome of an operation that returns a value: the value, or an <see cref="Error"/>.
/// A value or an error converts to a result on its own, so a service can write <c>return user;</c> or <c>return Error.NotFound(...)</c>.
/// </summary>
public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T? value, Error? error)
        : base(error)
    {
        _value = value;
    }

    /// <summary>The value of a successful result. Throws when the result is a failure.</summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"The result is a failure ({Error.Code}) and has no value.");

    public static Result<T> Success(T value) => new(value, null);

    public static new Result<T> Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T>(default, error);
    }

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(Error error) => Failure(error);

    public static Result<T> FromValue(T value) => Success(value);

    public static new Result<T> FromError(Error error) => Failure(error);

    /// <summary>Turns the result into one value, whichever side it is on. Handy at the API boundary.</summary>
    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<Error, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return IsSuccess ? onSuccess(Value) : onFailure(Error);
    }

    public override string ToString() => IsSuccess ? $"Success: {_value}" : $"Failure: {Error}";
}
