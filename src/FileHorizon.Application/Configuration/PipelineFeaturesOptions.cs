namespace FileHorizon.Application.Configuration;

public sealed class PipelineFeaturesOptions
{
    public const string SectionName = "Features";
    public bool EnableFileTransfer { get; set; } = false; // gate real file copy/move
}
