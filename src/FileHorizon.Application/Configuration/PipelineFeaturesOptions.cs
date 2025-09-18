namespace FileHorizon.Application.Configuration;

public sealed class PipelineFeaturesOptions
{
    public const string SectionName = "Features";
    public bool UseSyntheticPoller { get; set; } = true; // default keeps prior behavior
    public bool EnableFileTransfer { get; set; } = false; // gate real file copy/move
    public bool CopyInsteadOfMove { get; set; } = true; // default to copy (safer) unless explicitly moving
}
