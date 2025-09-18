namespace FileHorizon.Application.Configuration;

public sealed class FileSourceOptions
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Pattern { get; set; } = "*.*"; // glob-like pattern
    public bool Recursive { get; set; } = true;
    public bool MoveAfterProcessing { get; set; } = false; // future use
    public int MinStableSeconds { get; set; } = 2; // size/mtime stability window
    /// <summary>
    /// Optional destination root for files originating from this source. If provided it overrides the global FileDestination.RootPath.
    /// </summary>
    public string? DestinationPath { get; set; } = string.Empty;
    /// <summary>
    /// Whether to create the destination directory automatically if it does not exist. Defaults to true.
    /// </summary>
    public bool CreateDestinationDirectories { get; set; } = true;
}
