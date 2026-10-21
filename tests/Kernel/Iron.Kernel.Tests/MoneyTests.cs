namespace Iron.Kernel.Tests;

public class MoneyTests
{
    [Fact]
    [Proves("ADR-0007")]
    public void AddsAndSubtractsInTheSameCurrency()
    {
        var a = new Money(100.50m, Currency.Inr);
        var b = new Money(20.25m, Currency.Inr);

        Assert.Equal(new Money(120.75m, Currency.Inr), a + b);
        Assert.Equal(new Money(80.25m, Currency.Inr), a - b);
        Assert.Equal(new Money(201.00m, Currency.Inr), a * 2);
        Assert.Equal(new Money(201.00m, Currency.Inr), 2 * a);
        Assert.Equal(new Money(-100.50m, Currency.Inr), -a);
    }

    [Fact]
    [Proves("ADR-0007")]
    public void RefusesToMixCurrencies()
    {
        var rupees = new Money(10m, Currency.Inr);
        var dollars = new Money(10m, Currency.Usd);

        Assert.Throws<InvalidOperationException>(() => rupees + dollars);
        Assert.Throws<InvalidOperationException>(() => rupees.Subtract(dollars));
    }

    [Fact]
    [Proves("ADR-0007")]
    public void RoundsHalfAwayFromZero()
    {
        Assert.Equal(new Money(2.35m, Currency.Eur), new Money(2.345m, Currency.Eur).Round(2));
        Assert.Equal(new Money(-2.35m, Currency.Eur), new Money(-2.345m, Currency.Eur).Round(2));
        Assert.Equal(new Money(3m, Currency.Eur), new Money(2.5m, Currency.Eur).Round(0));
    }

    [Fact]
    [Proves("ADR-0007")]
    public void PrintsWithTwoDecimalsAndTheCode()
    {
        Assert.Equal("1234.50 INR", new Money(1234.5m, Currency.Inr).ToString());
        Assert.True(Money.Zero(Currency.Usd).IsZero);
        Assert.True(new Money(-1m, Currency.Usd).IsNegative);
    }

    [Theory]
    [Proves("ADR-0007")]
    [InlineData("INR", "INR")]
    [InlineData("usd", "USD")]
    [InlineData("Eur", "EUR")]
    public void CurrencyCodesAreThreeLettersStoredUppercase(string given, string expected)
    {
        Assert.Equal(expected, Currency.Parse(given).Code);
        Assert.Equal(Currency.Parse(expected), Currency.Parse(given));
    }

    [Theory]
    [Proves("ADR-0007")]
    [InlineData("")]
    [InlineData("IN")]
    [InlineData("INRR")]
    [InlineData("IN1")]
    [InlineData("I N")]
    public void BadCurrencyCodesAreRejected(string value)
    {
        Assert.False(Currency.IsValid(value));
        Assert.Throws<FormatException>(() => Currency.Parse(value));
    }
}
