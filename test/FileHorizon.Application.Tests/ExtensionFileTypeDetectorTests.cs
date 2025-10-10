using FileHorizon.Application.Infrastructure.Processing;
using Xunit;

namespace FileHorizon.Application.Tests;

public class ExtensionFileTypeDetectorTests
{
    [Theory]
    [InlineData("file.txt", "text/plain")]
    [InlineData("file.JSON", "application/json")]
    [InlineData("report.pdf", "application/pdf")]
    [InlineData("image.jpeg", "image/jpeg")]
    [InlineData("archive.zip", "application/zip")]
    public void Detect_Known_Extensions_Returns_Mime(string name, string expected)
    {
        var det = new ExtensionFileTypeDetector();
        var mime = det.Detect(name);
        Assert.Equal(expected, mime);
    }

    [Fact]
    public void Detect_Unknown_Returns_Null()
    {
        var det = new ExtensionFileTypeDetector();
        var mime = det.Detect("noextensionfile");
        Assert.Null(mime);
    }
}
