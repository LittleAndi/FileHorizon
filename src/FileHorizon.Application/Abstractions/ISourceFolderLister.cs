using FileHorizon.Application.Models;

namespace FileHorizon.Application.Abstractions;

/// <summary>
/// Lists the folders of each enabled source that the route inventory reports on. Implemented by the
/// pollers, which own the connection details for their sources.
/// </summary>
public interface ISourceFolderLister
{
    Task<IReadOnlyList<SourceFolderListing>> ListSourceFoldersAsync(CancellationToken ct);
}
