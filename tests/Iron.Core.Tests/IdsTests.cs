using Iron.Core.Identifiers;

namespace Iron.Core.Tests;

public class IdsTests
{
    [Fact]
    public void NewIdsAreVersionSevenAndUnique()
    {
        var first = Ids.New();
        var second = Ids.New();

        Assert.Equal(7, first.Version);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void IdsOrderByTheTimeTheyWereCreated()
    {
        var earlier = Ids.New(new FixedTimeProvider(new DateTimeOffset(2026, 10, 18, 10, 0, 0, TimeSpan.Zero)));
        var later = Ids.New(new FixedTimeProvider(new DateTimeOffset(2026, 10, 18, 10, 0, 1, TimeSpan.Zero)));

        Assert.True(later.CompareTo(earlier) > 0);
        Assert.True(string.CompareOrdinal(later.ToString(), earlier.ToString()) > 0);
    }

    [Fact]
    public void RejectsANullTimeProvider()
    {
        Assert.Throws<ArgumentNullException>(() => Ids.New(null!));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
