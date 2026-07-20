using FileHorizon.Application.Configuration;

namespace FileHorizon.Application.Tests;

public class IdempotencyOptionsValidatorTests
{
    private static readonly IdempotencyOptionsValidator Validator = new();

    [Fact]
    public void NegativeTtl_Fails()
    {
        var result = Validator.Validate(null, new IdempotencyOptions { TtlSeconds = -1 });
        Assert.True(result.Failed);
    }

    [Fact]
    public void ZeroTtl_Passes()
    {
        var result = Validator.Validate(null, new IdempotencyOptions { TtlSeconds = 0 });
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void WhitespaceDataDirectory_Fails()
    {
        var result = Validator.Validate(null, new IdempotencyOptions { DataDirectory = "   " });
        Assert.True(result.Failed);
    }

    [Fact]
    public void ValidDataDirectory_Passes()
    {
        var result = Validator.Validate(null, new IdempotencyOptions { DataDirectory = Path.GetTempPath() });
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void InvalidDataDirectory_Fails()
    {
        var result = Validator.Validate(null, new IdempotencyOptions { DataDirectory = "bad\0path" });
        Assert.True(result.Failed);
    }
}
