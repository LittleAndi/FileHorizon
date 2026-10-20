using System.Reflection;

namespace FileHorizon.Application.Common.Telemetry;

/// <summary>
/// Resolves the <c>service.version</c> resource attribute.
/// </summary>
public static class ServiceVersion
{
    /// <summary>
    /// The configured override when set; otherwise the assembly's informational version (stamped by
    /// MinVer from git tags, including the commit SHA); otherwise its assembly version.
    /// </summary>
    public static string Resolve(Assembly assembly, string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational)) return informational;

        return assembly.GetName().Version?.ToString() ?? "1.0.0";
    }
}
