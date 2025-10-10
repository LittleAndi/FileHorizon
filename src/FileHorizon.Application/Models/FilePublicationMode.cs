namespace FileHorizon.Application.Models;

/// <summary>
/// Mode that controls how file content is emitted when publishing.
/// </summary>
public enum FilePublicationMode
{
    /// <summary>Publish the entire file content in a single message. (Per-line mode removed: complexity deferred)</summary>
    WholeFile = 0,
}
