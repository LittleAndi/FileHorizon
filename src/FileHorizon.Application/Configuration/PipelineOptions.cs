namespace FileHorizon.Application.Configuration;

public enum PipelineRole
{
    All = 0,
    Poller = 1,
    Worker = 2
}

public sealed class PipelineOptions
{
    public PipelineRole Role { get; init; } = PipelineRole.All;
}
