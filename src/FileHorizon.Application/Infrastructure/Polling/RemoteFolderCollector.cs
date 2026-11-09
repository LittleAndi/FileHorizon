namespace FileHorizon.Application.Infrastructure.Polling;

/// <summary>
/// Builds the folder list for a remote source from a <c>ListTreeAsync</c> walk: every directory seen, plus
/// the root, keeping those that hold matching files or have no subfolders.
/// </summary>
internal sealed class RemoteFolderCollector
{
    private readonly string _root;
    private readonly bool _recursive;
    private readonly Dictionary<string, (bool HasFiles, bool HasChildren)> _folders = new(StringComparer.Ordinal);

    public RemoteFolderCollector(string root, bool recursive)
    {
        _root = Normalize(root);
        _recursive = recursive;
        _folders[_root] = (false, false);
    }

    public void AddDirectory(string path)
    {
        var dir = Normalize(path);
        _folders.TryAdd(dir, (false, false));
        if (Parent(dir) is { } parent) _folders[parent] = Get(parent) with { HasChildren = true };
    }

    public void AddFile(string path)
    {
        if (Parent(Normalize(path)) is { } dir) _folders[dir] = Get(dir) with { HasFiles = true };
    }

    // Listings are depth-first, so a parent is normally seen first; one that is not starts out empty.
    private (bool HasFiles, bool HasChildren) Get(string dir) => _folders.TryGetValue(dir, out var d) ? d : default;

    /// <summary>The folders to report, root first then in path order.</summary>
    public IReadOnlyList<string> Folders()
    {
        // A non-recursive source only ever polls its root.
        if (!_recursive) return [_root];
        return [.. _folders
            .Where(kv => kv.Value.HasFiles || !kv.Value.HasChildren)
            .Select(kv => kv.Key)
            .OrderBy(k => k == _root ? 0 : 1)
            .ThenBy(k => k, StringComparer.Ordinal)];
    }

    private static string Normalize(string path)
    {
        var trimmed = path.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    private static string? Parent(string path)
    {
        if (path == "/") return null;
        var idx = path.LastIndexOf('/');
        return idx switch
        {
            < 0 => null,
            0 => "/",
            _ => path[..idx]
        };
    }
}
