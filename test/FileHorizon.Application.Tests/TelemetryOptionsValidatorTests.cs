using FileHorizon.Application.Configuration;

namespace FileHorizon.Application.Tests;

public sealed class TelemetryOptionsValidatorTests
{
    private static readonly TelemetryOptionsValidator Validator = new();

    [Fact]
    public void Defaults_are_valid()
    {
        Assert.True(Validator.Validate(null, new TelemetryOptions()).Succeeded);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(1.0)]
    public void Accepts_sample_ratio_in_range(double ratio)
    {
        var result = Validator.Validate(null, new TelemetryOptions { TracesSampleRatio = ratio });
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Rejects_sample_ratio_out_of_range(double ratio)
    {
        var result = Validator.Validate(null, new TelemetryOptions { TracesSampleRatio = ratio });
        Assert.True(result.Failed);
        Assert.Contains("TracesSampleRatio", result.FailureMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Grpc")]
    [InlineData("grpc")]
    [InlineData("HttpProtobuf")]
    [InlineData("http/protobuf")]
    public void Accepts_known_protocols(string? protocol)
    {
        var result = Validator.Validate(null, new TelemetryOptions { OtlpProtocol = protocol });
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("Http-Protobuf")]
    [InlineData("Protobuf")]
    [InlineData("grcp")]
    public void Rejects_unknown_protocols(string protocol)
    {
        var result = Validator.Validate(null, new TelemetryOptions { OtlpProtocol = protocol });
        Assert.True(result.Failed);
        Assert.Contains("OtlpProtocol", result.FailureMessage);
    }

    [Theory]
    [InlineData("http://otel-collector:4317")]
    [InlineData("https://otel-collector:4318/custom/path")]
    public void Accepts_valid_endpoint_when_exporter_enabled(string endpoint)
    {
        var options = new TelemetryOptions { EnableOtlpExporter = true, OtlpEndpoint = endpoint };
        Assert.True(Validator.Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("otel-collector:4317")] // missing scheme parses as scheme "otel-collector"
    [InlineData("ftp://otel-collector:4317")]
    public void Rejects_missing_or_invalid_endpoint_when_exporter_enabled(string? endpoint)
    {
        var options = new TelemetryOptions { EnableOtlpExporter = true, OtlpEndpoint = endpoint };
        var result = Validator.Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains("OtlpEndpoint", result.FailureMessage);
    }

    [Fact]
    public void Endpoint_not_required_when_exporter_disabled()
    {
        var options = new TelemetryOptions { EnableOtlpExporter = false, OtlpEndpoint = null };
        Assert.True(Validator.Validate(null, options).Succeeded);
    }
}
