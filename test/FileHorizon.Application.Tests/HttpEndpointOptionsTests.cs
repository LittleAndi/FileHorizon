using FileHorizon.Application.Configuration;
using Microsoft.Extensions.Configuration;

namespace FileHorizon.Application.Tests;

public sealed class HttpEndpointOptionsTests
{
    private static HttpEndpointOptions Bind(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return configuration.GetSection(HttpEndpointOptions.SectionName).Get<HttpEndpointOptions>() ?? new HttpEndpointOptions();
    }

    [Fact]
    public void Enabled_by_default_when_section_is_absent()
    {
        Assert.True(Bind([]).Enabled);
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("true", true)]
    public void Binds_enabled_from_configuration(string value, bool expected)
    {
        Assert.Equal(expected, Bind(new() { ["Http:Enabled"] = value }).Enabled);
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    public void Prometheus_is_unreachable_only_when_http_is_off_and_prometheus_export_is_on(
        bool httpEnabled, bool enableMetrics, bool enablePrometheus, bool expected)
    {
        var http = new HttpEndpointOptions { Enabled = httpEnabled };
        var telemetry = new TelemetryOptions { EnableMetrics = enableMetrics, EnablePrometheus = enablePrometheus };

        Assert.Equal(expected, HttpEndpointOptions.IsPrometheusUnreachable(http, telemetry));
    }

    [Fact]
    public void Default_telemetry_with_http_disabled_is_unreachable()
    {
        // Prometheus is on by default, so disabling HTTP alone should trigger the startup warning.
        Assert.True(HttpEndpointOptions.IsPrometheusUnreachable(new HttpEndpointOptions { Enabled = false }, new TelemetryOptions()));
    }
}
