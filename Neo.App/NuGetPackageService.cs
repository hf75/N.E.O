using Neo.Agents;
using System.IO;

namespace Neo.App
{
    public interface INuGetPackageService
    {
        Task<NuGetResult> LoadPackagesAsync(Dictionary<string, string> packages, IEnumerable<string> existingDlls);
    }

    public record NuGetResult(List<string> DllPaths, Dictionary<string, string> PackageVersions);

    public class NuGetPackageService : INuGetPackageService
    {
        private readonly string _nuGetPackageDirectory;
        private readonly string? _coreRefPath;
        private readonly string? _desktopRefPath;

        public NuGetPackageService(string nuGetPackageDirectory, string? coreRefPath, string? desktopRefPath)
        {
            _nuGetPackageDirectory = nuGetPackageDirectory;
            _coreRefPath = coreRefPath;
            _desktopRefPath = desktopRefPath;

            // Sicherstellen, dass das Verzeichnis existiert
            if (!Directory.Exists(_nuGetPackageDirectory))
            {
                Directory.CreateDirectory(_nuGetPackageDirectory);
            }
        }

        public async Task<NuGetResult> LoadPackagesAsync(Dictionary<string, string> packages, IEnumerable<string> existingDlls)
        {
            var loadedDlls = new List<string>();
            var loadedVersions = new Dictionary<string, string>();

            if (packages != null && packages.Count > 0)
            {
                var agentLoader = new NuGetPackageLoaderAgent();
                agentLoader.SetInput("PackageNames", packages);
                agentLoader.SetInput("OutputDirectory", _nuGetPackageDirectory);
                agentLoader.SetInput("TargetFramework", $"net{Environment.Version.Major}.0-windows");

                await agentLoader.ExecuteAsync();

                var newDllPaths = agentLoader.GetOutput<List<string>>("DllPaths") ?? new List<string>();
                var newPackageVersions = agentLoader.GetOutput<Dictionary<string, string>>("PackageVersions") ?? new Dictionary<string, string>();

                var combinedDlls = existingDlls.Concat(newDllPaths).Distinct().ToList();
                RemoveExistingDefaultDlls(combinedDlls);

                return new NuGetResult(combinedDlls, newPackageVersions);
            }

            return new NuGetResult(existingDlls.ToList(), new Dictionary<string, string>());
        }

        private void RemoveExistingDefaultDlls(List<string> dlls)
        {
            var refDlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(_coreRefPath) && Directory.Exists(_coreRefPath))
            {
                foreach (var file in Directory.GetFiles(_coreRefPath, "*.dll"))
                {
                    refDlls.Add(Path.GetFileName(file));
                }
            }

            if (!string.IsNullOrEmpty(_desktopRefPath) && Directory.Exists(_desktopRefPath))
            {
                foreach (var file in Directory.GetFiles(_desktopRefPath, "*.dll"))
                {
                    refDlls.Add(Path.GetFileName(file));
                }
            }

            dlls.RemoveAll(nugetPath => refDlls.Contains(Path.GetFileName(nugetPath)));
        }
    }
}