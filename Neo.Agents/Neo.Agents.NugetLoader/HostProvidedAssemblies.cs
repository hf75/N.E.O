using System.Reflection;

namespace Neo.Agents
{
    /// <summary>
    /// Generated code runs inside a process that already has its UI framework loaded: the
    /// Avalonia the plugin window ships is the Avalonia the code will run on, whatever version a
    /// model — or a project saved with an older N.E.O. — asks NuGet for. A second Avalonia.Base
    /// with another version in the reference list makes every Avalonia type ambiguous, an error
    /// no model can fix by editing the code. So wherever a host knows which assemblies it
    /// provides, its copies win, and nothing it provides is downloaded (the Avalonia closure is
    /// 2.3 GB of native libraries for every platform, and takes half a minute):
    ///
    /// <list type="bullet">
    ///   <item><see cref="WithoutProvidedPackages"/> — a requested package the host provides is not
    ///         requested at all,</item>
    ///   <item><see cref="ProvidedPackages"/> — only remembered, for the hosts that export it for
    ///         other platforms,</item>
    ///   <item><see cref="Provides"/> — the NuGet loader stops at it when it follows the
    ///         dependencies of an add-on,</item>
    ///   <item><see cref="RemoveShadowing"/> — and assemblies that arrive inside other packages
    ///         are dropped.</item>
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

        /// <summary>
        /// True if the host provides the package: its main assembly (<c>&lt;PackageId&gt;.dll</c>), or, for a
        /// package without one, the assembly it only brings the native libraries or build tools for.
        /// </summary>
        public static bool Provides(IReadOnlyDictionary<string, string> hostIndex, string packageId)
        {
            if (string.IsNullOrWhiteSpace(packageId)) return false;
            var id = packageId.Trim();
            if (hostIndex.ContainsKey(id + ".dll")) return true;

            var native = id.IndexOf(".NativeAssets.", StringComparison.OrdinalIgnoreCase);
            if (native > 0) return hostIndex.ContainsKey(id[..native] + ".dll");

            return (id.StartsWith("Avalonia.Angle.", StringComparison.OrdinalIgnoreCase) || id.Equals("Avalonia.BuildServices", StringComparison.OrdinalIgnoreCase))
                && hostIndex.ContainsKey("Avalonia.dll");
        }

        /// <summary>
        /// The requested packages the host provides: nothing to download, but a project still names
        /// them, and an export for another platform restores them from there. Avalonia's own packages
        /// at the version the host ships, whatever was asked for.
        /// </summary>
        public static Dictionary<string, string> ProvidedPackages(
            IEnumerable<KeyValuePair<string, string>>? packages, IReadOnlyDictionary<string, string> hostIndex)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, version) in packages ?? [])
            {
                if (!Provides(hostIndex, id)) continue;
                var hostVersion = id.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase) && hostIndex.TryGetValue(id.Trim() + ".dll", out var path)
                    ? TryReadPackageVersion(path) : null;
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
