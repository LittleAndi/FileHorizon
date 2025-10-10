using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.Processing.Detection;
using FileHorizon.Application.Infrastructure.Processing;
using FileHorizon.Application.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FileHorizon.Application.Tests;

public class ContentDetectionTests
{
    private static CompositeFileTypeDetector CreateDetector(ContentDetectionOptions? opts = null)
    {
        opts ??= new ContentDetectionOptions();
        var optWrapper = Options.Create(opts);
        var extensionDetector = new ExtensionFileTypeDetector();
        return new CompositeFileTypeDetector(optWrapper, extensionDetector);
    }

    [Fact]
    public void Xml_Declaration_Detected_Without_Extension()
    {
        var detector = CreateDetector();
        var bytes = "<?xml version=\"1.0\"?><root/>"u8.ToArray();
        var mime = detector.Detect(null, bytes);
        Assert.Equal("application/xml", mime);
    }

    [Fact]
    public void Xml_No_Declaration_Detected()
    {
        var detector = CreateDetector();
        var bytes = "   <data attr=\"1\"></data>"u8.ToArray();
        var mime = detector.Detect("file.unknown", bytes);
        Assert.Equal("application/xml", mime);
    }

    [Fact]
    public void Edifact_UNA_UNB_Detected()
    {
        var detector = CreateDetector();
        var content = "UNA:+.? 'UNB+UNOA:1+SENDER+RECEIVER+210101:1200+1'"u8.ToArray();
        var mime = detector.Detect("orders.dat", content);
        Assert.Equal("application/edifact", mime);
    }

    [Fact]
    public void Edifact_UNB_Without_UNA_Detected()
    {
        var detector = CreateDetector();
        var content = "UNB+UNOA:1+SENDER+RECEIVER+210101:1200+1'UNH+1+ORDERS:D:96A:UN'"u8.ToArray();
        var mime = detector.Detect(null, content);
        Assert.Equal("application/edifact", mime);
    }

    [Fact]
    public void Extension_Overridden_By_Xml_Content()
    {
        var detector = CreateDetector();
        var content = "<?xml version=\"1.0\"?><root/>"u8.ToArray();
        var mime = detector.Detect("data.txt", content);
        Assert.Equal("application/xml", mime);
    }

    [Fact]
    public void Unknown_Content_Returns_Null()
    {
        var detector = CreateDetector();
        var content = "Just plain text without markers"u8.ToArray();
        var mime = detector.Detect(null, content);
        Assert.Null(mime);
    }

    [Fact]
    public void Edifact_Windows1252_Extended_Char_Detected()
    {
        var detector = CreateDetector();
        // Include Euro sign (0xE2 0x82 0xAC in UTF-8) in a free-text segment; in Windows-1252 this would be single 0x80 byte.
        // We simulate typical EDIFACT structure with UNB, UNH and segment terminators.
        var content = "UNB+UNOA:1+SENDER+RECEIVER+210101:1200+1'UNH+1+ORDERS:D:96A:UN'FTX+AAI+++PRICE € ADJUSTMENT'UNT+2+1'"u8.ToArray();
        var mime = detector.Detect("orders.dat", content);
        Assert.Equal("application/edifact", mime);
    }
}