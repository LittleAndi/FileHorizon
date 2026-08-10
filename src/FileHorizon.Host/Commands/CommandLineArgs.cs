namespace FileHorizon.Host.Commands;

/// <summary>
/// Minimal argument reading for the one-off maintenance commands. These flags deliberately bypass the
/// command-line configuration provider (see Program.cs), so they are parsed by hand.
/// </summary>
internal static class CommandLineArgs
{
    public static bool HasFlag(string[] args, string name)
        => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Value following <paramref name="name"/>, or null when the flag is absent or last.</summary>
    public static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }
}
