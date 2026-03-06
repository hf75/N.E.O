using System;
using System.IO;
using System.Reflection;

namespace Neo.AssemblyForge.Utils;

public enum AssemblyForgeAppHostTemplate
{
    WindowsExe,
    Linux,
    Osx,
}

public static class AppHostTemplates
{
    private const string ResourcePrefix = "Neo.AssemblyForge.Resources.AppHostTemplates.";

    private static readonly object Gate = new();

    public static string EnsureExtracted(AssemblyForgeAppHostTemplate template)
    {
        lock (Gate)
        {
            var extractionDir = Path.Combine(Path.GetTempPath(), "Neo.AssemblyForge", "AppHostTemplates");
            Directory.CreateDirectory(extractionDir);

            var fileName = template switch
            {
                AssemblyForgeAppHostTemplate.WindowsExe => "apphost-template-windows.exe",
                AssemblyForgeAppHostTemplate.Linux => "apphost-template-linux",
                AssemblyForgeAppHostTemplate.Osx => "apphost-template-osx",
                _ => "apphost-template-windows.exe",
            };

            var targetPath = Path.Combine(extractionDir, fileName);
            if (!IsValidExistingFile(targetPath))
                ExtractToFile(ResourcePrefix + fileName, targetPath);

            var licensePath = Path.Combine(extractionDir, "DOTNET-SDK-LICENSE.TXT");
            if (!IsValidExistingFile(licensePath))
                ExtractToFile(ResourcePrefix + "DOTNET-SDK-LICENSE.TXT", licensePath);

            return targetPath;
        }
    }

    private static bool IsValidExistingFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;

            var info = new FileInfo(path);
            return info.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void ExtractToFile(string resourceName, string targetPath)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            throw new FileNotFoundException($"Embedded resource '{resourceName}' not found.", resourceName);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);

        using var file = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        stream.CopyTo(file);
    }
}

