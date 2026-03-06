using System;
using System.Collections.Generic;
using System.Linq;

namespace Neo.AssemblyForge;

public sealed record VirtualFile(string Path, string Content);

public sealed class VirtualProject
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    public bool AddFile(string path, string content)
        => _files.TryAdd(path, content ?? string.Empty);

    public bool DeleteFile(string path)
        => _files.Remove(path);

    public bool UpdateFileContent(string path, string newContent)
    {
        if (!_files.ContainsKey(path))
            return false;

        _files[path] = newContent ?? string.Empty;
        return true;
    }

    public bool RenameFile(string oldPath, string newPath)
    {
        if (_files.ContainsKey(newPath) || !_files.TryGetValue(oldPath, out var content))
            return false;

        _files.Remove(oldPath);
        _files.Add(newPath, content);
        return true;
    }

    public bool FileExists(string path) => _files.ContainsKey(path);

    public string? GetFileContent(string path)
        => _files.TryGetValue(path, out var content) ? content : null;

    public IEnumerable<VirtualFile> GetAllFiles()
        => _files.Select(kvp => new VirtualFile(kvp.Key, kvp.Value));

    public IEnumerable<string> GetAllFilePaths()
        => _files.Keys;

    public List<string> GetSourceCodeAsStrings(
        IDictionary<string, string>? temporaryReplacements = null,
        IEnumerable<string>? skipList = null)
    {
        return GetCombinedFilesView(temporaryReplacements, skipList)
            .Where(f => f.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Value)
            .ToList();
    }

    private IEnumerable<KeyValuePair<string, string>> GetCombinedFilesView(
        IDictionary<string, string>? temporaryReplacements,
        IEnumerable<string>? skipList)
    {
        IEnumerable<KeyValuePair<string, string>> view = _files;

        if (temporaryReplacements != null && temporaryReplacements.Count > 0)
        {
            var merged = new Dictionary<string, string>(_files, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in temporaryReplacements)
                merged[kv.Key] = kv.Value ?? string.Empty;
            view = merged;
        }

        if (skipList != null)
        {
            var skipped = new HashSet<string>(skipList, StringComparer.OrdinalIgnoreCase);
            view = view.Where(f => !skipped.Contains(f.Key));
        }

        return view;
    }
}
