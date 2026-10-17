using Iron.Core.Results;

namespace Iron.Core.Tests;

public class ResultTests
{
    private static readonly Error SampleError = Error.NotFound("tests.sample.missing", "The sample is missing.");

    [Fact]
    public void ASuccessHasNoError()
    {
        var result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Throws<InvalidOperationException>(() => result.Error);
    }

    [Fact]
    public void AFailureCarriesItsError()
    {
        var result = Result.Failure(SampleError);

        Assert.True(result.IsFailure);
        Assert.Same(SampleError, result.Error);
    }

    [Fact]
    public void AValueConvertsToASuccessfulResult()
    {
        Result<int> result = 42;

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void AnErrorConvertsToAFailedResult()
    {
        Result<int> result = SampleError;

        Assert.True(result.IsFailure);
        Assert.Same(SampleError, result.Error);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void AFailedPlainResultCanBeMadeFromAnError()
    {
        Result result = SampleError;

        Assert.True(result.IsFailure);
        Assert.Equal("tests.sample.missing", result.Error.Code);
    }

    [Fact]
    public void MatchPicksTheSideThatApplies()
    {
        var success = Result<int>.Success(2);
        var failure = Result<int>.Failure(SampleError);

        Assert.Equal("value 2", success.Match(v => $"value {v}", e => $"error {e.Code}"));
        Assert.Equal("error tests.sample.missing", failure.Match(v => $"value {v}", e => $"error {e.Code}"));
    }

    [Fact]
    public void FailureRejectsANullError()
    {
        Assert.Throws<ArgumentNullException>(() => Result.Failure(null!));
        Assert.Throws<ArgumentNullException>(() => Result<int>.Failure(null!));
    }
}
