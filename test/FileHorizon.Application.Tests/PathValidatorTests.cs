using FileHorizon.Application.Common;

namespace FileHorizon.Application.Tests;

public class PathValidatorTests
{
    [Theory]
    [InlineData("C:/Data/file.txt")]
    [InlineData("C:\\Data\\file.txt")]
    [InlineData("/var/log/app.log")]
    [InlineData("\\\\server\\share\\folder\\file.bin")] // UNC
    public void IsValidLocalPath_ReturnsTrue_ForAbsoluteVariants(string path)
    {
        var ok = PathValidator.IsValidLocalPath(path, out var error);
        Assert.True(ok, error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/path.txt")]
    [InlineData("relative\\path.txt")]
    [InlineData("C:relative")]
    [InlineData("C|:/bad.txt")]
    public void IsValidLocalPath_ReturnsFalse_ForInvalid(string path)
    {
        var ok = PathValidator.IsValidLocalPath(path, out var error);
        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("/")] // root
    [InlineData("/inbox")]
    [InlineData("/inbox/file.txt")]
    [InlineData("/deep/nested/path/file.bin")]
    public void IsValidRemotePath_ReturnsTrue_ForValid(string path)
    {
        var ok = PathValidator.IsValidRemotePath(path, out var error);
        Assert.True(ok, error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative")]
    [InlineData("\\wrong\\style")]
    [InlineData("/bad\\mix")]
    public void IsValidRemotePath_ReturnsFalse_ForInvalid(string path)
    {
        var ok = PathValidator.IsValidRemotePath(path, out var error);
        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
