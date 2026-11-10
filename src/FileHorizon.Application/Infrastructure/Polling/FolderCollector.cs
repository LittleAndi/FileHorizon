namespace FileHorizon.Application.Infrastructure.Polling;

/// <summary>
/// Builds a source's folder list for the route inventory from one walk of its tree: every directory seen,
/// plus the root, keeping those that hold matching files or have no subfolders. Shared by the local and
/// remote pollers so both report the same folders for the same tree shape.
/// </summary>
internal sealed class FolderCollector
{
    private readonly char _separator;
    private readonly bool _recursive;
    private readonly Dictionary<string, (bool HasFiles, bool HasChildren)> _folders = new(StringComparer.Ordinal);
    private string _root;
    private bool _rootConfirmed;

    public FolderCollector(string root, char separator, bool recursive)
    {
        _separator = separator;
        _recursive = recursive;
        _root = Normalize(root);
        _folders[_root] = (false, false);
    }

    public void AddDirectory(string path)
    {
        var dir = Normalize(path);
        ConfirmRoot(dir);
        _folders.TryAdd(dir, (false, false));
        if (Parent(dir) is { } parent) _folders[parent] = Get(parent) with { HasChildren = true };
    }

    public void AddFile(string path)
    {
        var file = Normalize(path);
        ConfirmRoot(file);
        if (Parent(file) is { } dir) _folders[dir] = Get(dir) with { HasFiles = true };
    }

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

    /// <summary>
    /// A server can report entries under a different path than the configured root: SSH.NET lists the
    /// canonical path (symlinks resolved), so a symlinked root would never link to its children. The
    /// first entry is a direct child of the root as the server sees it, so adopt that parent as the root.
    /// </summary>
    private void ConfirmRoot(string entry)
    {
        if (_rootConfirmed) return;
        _rootConfirmed = true;
        if (Parent(entry) is not { } serverRoot || serverRoot == _root || IsUnder(entry, _root)) return;
        _folders.Remove(_root);
        _root = serverRoot;
        _folders.TryAdd(_root, (false, false));
    }

    // Listings are depth-first, so a parent is normally seen first; one that is not starts out empty.
    private (bool HasFiles, bool HasChildren) Get(string dir) => _folders.TryGetValue(dir, out var d) ? d : default;

    private bool IsUnder(string path, string dir)
        => path.StartsWith(dir.EndsWith(_separator) ? dir : dir + _separator, StringComparison.Ordinal);

    private string Normalize(string path)
    {
        // Collapse "/a//b" on '/' paths only: on Windows a leading "\\" is a UNC share, not a typo.
        if (_separator == '/')
        {
            while (path.Contains("//", StringComparison.Ordinal)) path = path.Replace("//", "/", StringComparison.Ordinal);
        }
        var trimmed = path.TrimEnd(_separator);
        return trimmed.Length == 0 ? _separator.ToString() : trimmed;
    }

    private string? Parent(string path)
    {
        if (path.Length == 1 && path[0] == _separator) return null;
        var idx = path.LastIndexOf(_separator);
        return idx switch
        {
            < 0 => null,
            0 => _separator.ToString(),
            _ => path[..idx]
        };
    }
}
