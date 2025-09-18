namespace FileHorizon.Application.Configuration;

public sealed class FileSourceOptions
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Pattern { get; set; } = "*.*"; // glob-like pattern
    public bool Recursive { get; set; } = true;
    public bool MoveAfterProcessing { get; set; } = false; // future use
    public int MinStableSeconds { get; set; } = 2; // size/mtime stability window
}
