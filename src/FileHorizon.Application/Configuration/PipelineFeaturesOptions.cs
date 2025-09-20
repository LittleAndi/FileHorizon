namespace FileHorizon.Application.Configuration;

public sealed class PipelineFeaturesOptions
{
    public const string SectionName = "Features";
    public bool EnableFileTransfer { get; set; } = false; // gate real file copy/move
    /// <summary>
    /// Master switch for any polling activity (synthetic or directory). When false, no poller will enqueue new events.
    /// </summary>
    public bool EnablePolling { get; set; } = true;

    /// <summary>
    /// Master switch for processing dequeued events. When false, handlers may still dequeue (future refinement) but will no-op actual processing.
    /// </summary>
    public bool EnableProcessing { get; set; } = true;
}
