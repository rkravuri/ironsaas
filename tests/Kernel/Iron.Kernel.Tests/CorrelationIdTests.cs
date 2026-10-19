using System.Diagnostics;

namespace Iron.Kernel.Tests;

public class CorrelationIdTests
{
    [Fact]
    public void ANewIdIsThirtyTwoLowercaseHexCharacters()
    {
        var id = CorrelationId.New();

        Assert.Equal(CorrelationId.Length, id.Value.Length);
        Assert.True(CorrelationId.TryParse(id.Value, out var parsed));
        Assert.Equal(id, parsed);
    }

    [Fact]
    public void CurrentUsesTheActiveTrace()
    {
        using var activity = new Activity("test").Start();

        var id = CorrelationId.Current();

        Assert.Equal(activity.TraceId.ToHexString(), id.Value);
    }

    [Fact]
    public void CurrentMakesANewIdWhenNoTraceIsActive()
    {
        Assert.Null(Activity.Current);

        var id = CorrelationId.Current();

        Assert.False(id.IsEmpty);
        Assert.Equal(CorrelationId.Length, id.Value.Length);
    }

    [Theory]
    [InlineData("0af7651916cd43dd8448eb211c80319c", true)]
    [InlineData("0AF7651916CD43DD8448EB211C80319C", true)]
    [InlineData("0af7651916cd43dd8448eb211c80319", false)]
    [InlineData("0af7651916cd43dd8448eb211c80319cz", false)]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ParsesOnlyHexStringsOfTheRightLength(string? value, bool expected)
    {
        Assert.Equal(expected, CorrelationId.TryParse(value, out var id));
        if (expected)
        {
            Assert.Equal(value!.ToLowerInvariant(), id.Value);
        }
    }
}
