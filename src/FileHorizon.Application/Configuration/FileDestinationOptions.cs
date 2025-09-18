namespace FileHorizon.Application.Configuration;

public sealed class FileDestinationOptions
{
    public const string SectionName = "FileDestination";
    public string RootPath { get; set; } = string.Empty;
    public bool CreateDirectories { get; set; } = true;
}
