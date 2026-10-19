using System.Globalization;

namespace Iron.Kernel;

/// <summary>
/// An amount in one currency. Arithmetic works only between the same currency; mixing currencies throws, because
/// that is always a programming error. Rounding is commercial (half away from zero), as on invoices.
/// </summary>
public readonly record struct Money(decimal Amount, Currency Currency)
{
    public static Money Zero(Currency currency) => new(0m, currency);

    public bool IsZero => Amount == 0m;

    public bool IsNegative => Amount < 0m;

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount + other.Amount, Currency);
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount - other.Amount, Currency);
    }

    public Money Multiply(decimal factor) => new(Amount * factor, Currency);

    public Money Negate() => new(-Amount, Currency);

    /// <summary>Rounds to the given number of decimals, half away from zero.</summary>
    public Money Round(int decimals) => new(Math.Round(Amount, decimals, MidpointRounding.AwayFromZero), Currency);

    public static Money operator +(Money left, Money right) => left.Add(right);

    public static Money operator -(Money left, Money right) => left.Subtract(right);

    public static Money operator *(Money money, decimal factor) => money.Multiply(factor);

    public static Money operator *(decimal factor, Money money) => money.Multiply(factor);

    public static Money operator -(Money money) => money.Negate();

    public override string ToString()
        => Amount.ToString("0.00", CultureInfo.InvariantCulture) + " " + Currency.Code;

    private void EnsureSameCurrency(Money other)
    {
        if (Currency != other.Currency)
        {
            throw new InvalidOperationException(
                $"Cannot combine {Currency.Code} with {other.Currency.Code}. Convert first.");
        }
    }
}
