using System.Text;

namespace FileHorizon.Application.Common;

/// <summary>
/// Provides lightweight validation for local (Windows / POSIX) and remote (FTP/SFTP) paths.
/// Intentionally does NOT normalize or mutate inputs; callers should surface failures early (at configuration bind time).
/// </summary>
public static class PathValidator
{
    private static readonly char[] WindowsInvalidChars = ['<', '>', '"', '|', '?', '*'];

    public static bool IsValidLocalPath(string? path, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(path)) { error = "Path is empty or whitespace"; return false; }
        path = path.Trim();

        if (!(IsWindowsDrivePath(path) || IsUnc(path) || IsPosixAbsolute(path)))
        {
            error = "Local path must be absolute (drive-rooted, UNC, or /-rooted)";
            return false;
        }

        for (int i = 0; i < path.Length; i++)
        {
            var c = path[i];
            if (c == ':' && i != 1 && IsPosixAbsolute(path))
            {
                // Allow colon only after drive letter in Windows drive path. If POSIX absolute, colon is allowed in names.
                continue; // treat as allowed in POSIX style
            }
            if (c == ':' && i != 1 && IsWindowsDrivePath(path))
            {
                error = "Colon ':' only permitted after drive letter (e.g. C:)";
                return false;
            }
            if (WindowsInvalidChars.Contains(c))
            {
                error = $"Local path contains invalid character '{c}'";
                return false;
            }
            if (char.IsControl(c))
            {
                error = "Local path contains control characters";
                return false;
            }
        }

        return true;
    }

    public static bool IsValidRemotePath(string? path, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(path)) { error = "Remote path is empty or whitespace"; return false; }
        path = path.Trim();
        if (!path.StartsWith('/')) { error = "Remote path must start with '/'"; return false; }
        if (path.Contains('\\')) { error = "Remote path must not contain backslashes"; return false; }
        if (path.Any(char.IsControl)) { error = "Remote path contains control characters"; return false; }
        return true;
    }

    private static bool IsWindowsDrivePath(string p) =>
        p.Length >= 3 && char.IsLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/');

    private static bool IsUnc(string p) => p.StartsWith("\\\\");
    private static bool IsPosixAbsolute(string p) => p.StartsWith('/');
}
