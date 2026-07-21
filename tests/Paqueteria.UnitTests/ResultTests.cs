using Paqueteria.Domain;

namespace Paqueteria.UnitTests;

public sealed class ResultTests
{
    [Fact]
    public void Success_exposes_the_value_and_no_error()
    {
        var result = Result<int>.Success(42);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
        Assert.Equal(Error.None, result.Error);
    }

    [Fact]
    public void Failure_exposes_the_error_and_rejects_value_access()
    {
        var error = new Error("foundation.invalid", "The value is invalid.");
        var result = Result<int>.Failure(error);

        Assert.True(result.IsFailure);
        Assert.Equal(error, result.Error);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void Failure_requires_a_non_empty_error()
    {
        Assert.Throws<ArgumentException>(() => Result.Failure(Error.None));
    }
}
