using System;
using System.Diagnostics;
using System.IO;

namespace Neo.AssemblyForge.Utils;

public static class FileSystemHelper
{
    public static void ClearDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        try
        {
            var directory = new DirectoryInfo(path);

            foreach (var file in directory.GetFiles())
                file.Delete();

            foreach (var dir in directory.GetDirectories())
                dir.Delete(true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClearDirectory] Best-effort cleanup failed for '{path}': {ex.Message}");
        }
    }

    public static string EnsureDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Value cannot be null/empty.", nameof(path));

        Directory.CreateDirectory(path);
        return path;
    }
}
