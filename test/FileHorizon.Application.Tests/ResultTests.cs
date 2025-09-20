namespace FileHorizon.Application.Tests;

using FileHorizon.Application.Common;

public class ResultTests
{
    [Fact]
    public void Success_Result_Should_Have_IsSuccess_True()
    {
        var r = Result.Success();
        Assert.True(r.IsSuccess);
        Assert.False(r.IsFailure);
    }

    [Fact]
    public void Failure_Result_Should_Have_IsFailure_True()
    {
        var r = Result.Failure(Error.File.SizeUnstable);
        Assert.True(r.IsFailure);
        Assert.False(r.IsSuccess);
        Assert.Equal(Error.File.SizeUnstable, r.Error);
    }

    [Fact]
    public void Generic_Result_Success_Should_Expose_Value()
    {
        var r = Result<string>.Success("abc");
        Assert.True(r.IsSuccess);
        Assert.Equal("abc", r.Value);
    }
}