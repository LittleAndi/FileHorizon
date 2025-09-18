namespace FileHorizon.Application.Configuration;

public sealed class FileSourcesOptions
{
    public const string SectionName = "FileSources";
    public List<FileSourceOptions> Sources { get; set; } = new();
}
