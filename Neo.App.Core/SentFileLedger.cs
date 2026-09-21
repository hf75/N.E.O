using System.IO;

namespace Neo.App
{
    /// <summary>
    /// Which dependency files a plugin window has received. The window keeps every assembly it
    /// was sent until it exits, so a hot reload that brings a new NuGet package only has to
    /// stream what is new — before the update, because the patched code may touch it at once.
    /// Clear it whenever a new child process starts.
    /// </summary>
    public sealed class SentFileLedger
    {
        private readonly HashSet<string> _sent = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        public void Clear()
        {
            lock (_lock) _sent.Clear();
        }

        public void MarkSent(string path)
        {
            if (Key(path) is { } key)
                lock (_lock) _sent.Add(key);
        }

        /// <summary>The existing files among <paramref name="paths"/> the window has not received (full paths, no duplicates).</summary>
        public List<string> Unsent(IEnumerable<string> paths)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (_lock)
            {
                foreach (var path in paths)
                {
                    if (Key(path) is { } key && !_sent.Contains(key) && seen.Add(key))
                        result.Add(Path.GetFullPath(path));
                }
            }
            return result;
        }

        // The same file with the same content: path, size and time of the last write.
        private static string? Key(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
