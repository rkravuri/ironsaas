using System.Diagnostics.CodeAnalysis;

namespace Iron.Kernel;

/// <summary>An ISO 4217 currency code such as INR, USD or EUR: exactly three ASCII letters, stored in uppercase.</summary>
public readonly record struct Currency
{
    public static readonly Currency Inr = new("INR");
    public static readonly Currency Usd = new("USD");
    public static readonly Currency Eur = new("EUR");
    public static readonly Currency Gbp = new("GBP");
    public static readonly Currency Sar = new("SAR");
    public static readonly Currency Aed = new("AED");

    private readonly string? _code;

    private Currency(string code)
    {
        _code = code;
    }

    public string Code => _code ?? string.Empty;

    public bool IsEmpty => _code is null;

    public static Currency Parse(string code)
    {
        return TryParse(code, out var currency)
            ? currency
            : throw new FormatException($"'{code}' is not a currency code. Use three letters such as INR.");
    }

    public static bool TryParse(string? code, out Currency currency)
    {
        if (IsValid(code))
        {
            currency = new Currency(code.ToUpperInvariant());
            return true;
        }

        currency = default;
        return false;
    }

    public static bool IsValid([NotNullWhen(true)] string? code)
    {
        if (code is null || code.Length != 3)
        {
            return false;
        }

        foreach (var character in code)
        {
            if (!char.IsAsciiLetter(character))
            {
                return false;
            }
        }

        return true;
    }

    public override string ToString() => Code;
}
