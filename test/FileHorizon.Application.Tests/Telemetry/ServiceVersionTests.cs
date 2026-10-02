using System.Reflection;
using FileHorizon.Application.Common.Telemetry;

namespace FileHorizon.Application.Tests.Telemetry;

public sealed class ServiceVersionTests
{
    private static readonly Assembly ApplicationAssembly = typeof(ServiceVersion).Assembly;

    [Fact]
    public void Configured_Value_Takes_Precedence()
    {
        Assert.Equal("2026.10.02-abc", ServiceVersion.Resolve(ApplicationAssembly, "2026.10.02-abc"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Falls_Back_To_Informational_Version(string? configured)
    {
        var expected = ApplicationAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        Assert.Equal(expected, ServiceVersion.Resolve(ApplicationAssembly, configured));
    }

    [Fact]
    public void Shipped_Assembly_Is_Stamped_By_MinVer()
    {
        // MinVer always sets a semantic version; without it the SDK default is 1.0.0.
        var version = ServiceVersion.Resolve(ApplicationAssembly);

        Assert.DoesNotMatch(@"^1\.0\.0(\.0)?$", version);
    }
}
