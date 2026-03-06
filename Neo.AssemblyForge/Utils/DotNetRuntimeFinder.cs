using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Neo.AssemblyForge.Utils;

public enum DotNetRuntimeType
{
    NetCoreApp,
    WindowsDesktopApp,
}

public static class DotNetRuntimeFinder
{
    public static string? GetHighestRuntimePath(DotNetRuntimeType runtimeType, int? majorVersionFilter = null)
    {
        string runtimeName = runtimeType switch
        {
            DotNetRuntimeType.NetCoreApp => "Microsoft.NETCore.App",
            DotNetRuntimeType.WindowsDesktopApp => "Microsoft.WindowsDesktop.App",
            _ => "Microsoft.NETCore.App",
        };

        string? highestVersionPath = null;
        Version? highestVersion = null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "--list-runtimes",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = psi };
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
                return null;

            var escapedRuntimeName = Regex.Escape(runtimeName);
            var regex = new Regex($@"^{escapedRuntimeName}\s+([\d\.]+)\s+\[(.*?)\]", RegexOptions.Multiline);
            var matches = regex.Matches(output);

            foreach (Match match in matches)
            {
                if (match.Groups.Count != 3)
                    continue;

                string versionString = match.Groups[1].Value;
                string basePath = match.Groups[2].Value;

                if (!Version.TryParse(versionString, out var currentVersion))
                    continue;

                if (majorVersionFilter.HasValue && currentVersion.Major != majorVersionFilter.Value)
                    continue;

                if (highestVersion == null || currentVersion > highestVersion)
                {
                    highestVersion = currentVersion;
                    highestVersionPath = Path.Combine(basePath, versionString);
                }
            }
        }
        catch
        {
            return null;
        }

        if (highestVersionPath != null && Directory.Exists(highestVersionPath))
            return highestVersionPath;

        return null;
    }
}
