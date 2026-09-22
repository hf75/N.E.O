using System.IO;

namespace Neo.App
{
    /// <summary>
    /// The folder this N.E.O. builds apps and keeps NuGet packages in. The first instance on a
    /// machine uses the usual one; a second instance running at the same time (two people in a
    /// shared session on one PC, or just two windows) gets its own under <c>Instances\n</c>, so the
    /// two never compile over each other's DLL or clear each other's packages at start-up.
    /// </summary>
    public static class InstanceFolder
    {
        private static readonly object s_gate = new();
        private static readonly List<FileStream> s_locks = new();

        /// <summary>A folder under <paramref name="root"/> no other N.E.O. uses; each call takes a new one.</summary>
        public static string Acquire(string root)
        {
            lock (s_gate)
            {
                var candidates = new[] { root }.Concat(Enumerable.Range(1, 9).Select(i => Path.Combine(root, "Instances", i.ToString())));
                foreach (var folder in candidates)
                {
                    try
                    {
                        Directory.CreateDirectory(folder);
                        // Held until the process ends; the OS releases it even after a crash.
                        var lockFile = new FileStream(Path.Combine(folder, ".instance.lock"), FileMode.OpenOrCreate,
                            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                        s_locks.Add(lockFile);
                        return folder;
                    }
                    catch (IOException) { /* in use by another N.E.O. */ }
                    catch (UnauthorizedAccessException) { /* in use, or not ours */ }
                }

                var fallback = Path.Combine(root, "Instances", Environment.ProcessId.ToString());
                Directory.CreateDirectory(fallback);
                return fallback;
            }
        }
    }
}
