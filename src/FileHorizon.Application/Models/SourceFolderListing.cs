namespace FileHorizon.Application.Models;

/// <summary>
/// The folders of one source that files can arrive in, for the route inventory.
/// </summary>
/// <param name="Source">The configured source name.</param>
/// <param name="Protocol">The protocol as it appears on file events (local, ftp, sftp).</param>
/// <param name="Pattern">The source's file pattern.</param>
/// <param name="Separator">The path separator used in <paramref name="Folders"/>.</param>
/// <param name="Folders">
/// Identity keys of the folders to report, in the form the poller builds for files: the folders that hold
/// files matching <paramref name="Pattern"/> and those with no subfolders. Folders that only contain other
/// folders are left out, so a tree of partner folders does not report every level above the leaves.
/// </param>
/// <param name="Error">Why the source could not be listed, or null on success.</param>
public sealed record SourceFolderListing(
    string Source,
    string Protocol,
    string Pattern,
    char Separator,
    IReadOnlyList<string> Folders,
    string? Error = null)
{
    public static SourceFolderListing Failed(string source, string protocol, string pattern, char separator, string error)
        => new(source, protocol, pattern, separator, [], error);
}
