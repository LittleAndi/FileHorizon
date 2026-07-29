namespace FileHorizon.Application.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that reports as skipped off Windows, for behaviour the OS itself
/// decides. xunit v2 has no runtime skip, and returning early would report a pass for a test that
/// never ran.
/// </summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute(string reason)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = reason;
        }
    }
}
