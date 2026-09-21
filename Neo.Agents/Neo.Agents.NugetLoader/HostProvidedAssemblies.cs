using System.Reflection;

namespace Neo.Agents
{
    /// <summary>
    /// Generated code runs inside a process that already has its UI framework loaded: the
    /// Avalonia the plugin window ships is the Avalonia the code will run on, whatever version a
    /// model — or a project saved with an older N.E.O. — asks NuGet for. A second Avalonia.Base
    /// with another version in the reference list makes every Avalonia type ambiguous, an error
    /// no model can fix by editing the code. So wherever a host knows which assemblies it
    /// provides, its copies win:
    ///
    /// <list type="bullet">
    ///   <item><see cref="WithHostVersions"/> — a requested package the host provides is moved to
    ///         the host's version (hosts that still resolve the framework through NuGet because
    ///         they also export it for other platforms),</item>
    ///   <item><see cref="WithoutProvidedPackages"/> — or is not requested at all (hosts that
    ///         reference their own copies only),</item>
    ///   <item><see cref="RemoveShadowing"/> — and assemblies that arrive as dependencies of other
    ///         packages (NuGet picks the lowest version an add-on allows) are dropped.</item>
    /// </list>
    /// </summary>
    public static class HostProvidedAssemblies
    {
        /// <summary>File name (<c>Avalonia.Base.dll</c>) → path, for the assemblies the host provides.</summary>
        public static Dictionary<string, string> Index(IEnumerable<string>? hostDllPaths)
        {
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in hostDllPaths ?? [])
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                var name = Path.GetFileName(path.Trim());
                if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    byName.TryAdd(name, path.Trim());
            }
            return byName;
        }

        /// <summary>
        /// Like <see cref="Index"/>, but only for the DLLs that do not come out of the NuGet output
        /// folder: in a list that mixes both, those are the ones the host registered itself.
        /// </summary>
        public static Dictionary<string, string> IndexOutside(IEnumerable<string>? dllPaths, string nuGetOutputDirectory)
        {
            var nugetRoot = Path.GetFullPath(nuGetOutputDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            return Index((dllPaths ?? []).Where(p =>
                !string.IsNullOrWhiteSpace(p) &&
                !Path.GetFullPath(p.Trim()).StartsWith(nugetRoot, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>True if the package's main assembly (<c>&lt;PackageId&gt;.dll</c>) is provided by the host.</summary>
        public static bool Provides(IReadOnlyDictionary<string, string> hostIndex, string packageId)
            => !string.IsNullOrWhiteSpace(packageId) && hostIndex.ContainsKey(packageId.Trim() + ".dll");

        /// <summary>The requested packages, with every package the host provides moved to the host's version.</summary>
        public static Dictionary<string, string> WithHostVersions(
            IEnumerable<KeyValuePair<string, string>>? packages, IReadOnlyDictionary<string, string> hostIndex)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, version) in packages ?? [])
            {
                var hostVersion = Provides(hostIndex, id) ? TryReadPackageVersion(hostIndex[id.Trim() + ".dll"]) : null;
                result[id] = hostVersion ?? version;
            }
            return result;
        }

        /// <summary>The requested packages without those the host provides.</summary>
        public static Dictionary<string, string> WithoutProvidedPackages(
            IEnumerable<KeyValuePair<string, string>>? packages, IReadOnlyDictionary<string, string> hostIndex)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, version) in packages ?? [])
            {
                if (!Provides(hostIndex, id))
                    result[id] = version;
            }
            return result;
        }

        /// <summary>Removes resolved DLLs the host provides under the same file name. Returns how many.</summary>
        public static int RemoveShadowing(List<string> resolvedDllPaths, IReadOnlyDictionary<string, string> hostIndex)
            => resolvedDllPaths.RemoveAll(p =>
                hostIndex.TryGetValue(Path.GetFileName(p), out var hostPath) &&
                !string.Equals(Path.GetFullPath(p), Path.GetFullPath(hostPath), StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The NuGet version that belongs to an assembly the host ships: for the packages in
        /// question (Avalonia and its first-party add-ons) that is the assembly version without
        /// the revision, 12.1.2.0 → 12.1.2.
        /// </summary>
        private static string? TryReadPackageVersion(string dllPath)
        {
            try
            {
                var v = AssemblyName.GetAssemblyName(dllPath).Version;
                return v == null ? null : $"{v.Major}.{v.Minor}.{v.Build}";
            }
            catch
            {
                return null;
            }
        }
    }
}
