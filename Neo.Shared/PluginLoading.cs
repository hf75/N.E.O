using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;

namespace Neo.Shared
{
    /// <summary>
    /// Collectible load context for a generated plugin that is loaded from bytes only — no file
    /// paths, so nothing on disk gets locked between compile iterations and a sandboxed child
    /// needs no read access to the host's build or NuGet folders.
    ///
    /// <para><see cref="Load"/> returns <c>null</c> on purpose: everything the default context
    /// already knows (the framework, the UI toolkit of the plugin window) resolves there, and
    /// everything else comes from the byte bundle via the <c>Resolving</c> events.</para>
    ///
    /// Shared by every plugin window (WPF and Avalonia, desktop and MCP heads).
    /// </summary>
    public sealed class SandboxPluginLoadContext : AssemblyLoadContext, IDisposable
    {
        private readonly Func<AssemblyName, byte[]?> _managedResolver;
        private readonly Func<string, string> _nativePathProvider;

        public SandboxPluginLoadContext(
            Func<AssemblyName, byte[]?> managedResolver,
            Func<string, string> nativePathProvider)
            // Collectible, so a replaced plugin can be unloaded — except when the process can hot
            // reload: edit-and-continue on an assembly in a collectible context crashes the runtime
            // as soon as an update adds a static field (access violation in the type's .cctor).
            // Those processes patch the plugin in place most of the time and only leak an assembly
            // on the rarer full reloads.
            : base(isCollectible: !HotReloadRuntime.IsSupported)
        {
            _managedResolver = managedResolver;
            _nativePathProvider = nativePathProvider;

            Resolving += OnResolveManaged;
            ResolvingUnmanagedDll += OnResolveUnmanaged;
        }

        /// <summary>
        /// Creates a context over a byte bundle as the plugin windows receive it over the pipe.
        /// </summary>
        /// <param name="managedAssembliesByFullName">Assembly.FullName → bytes.</param>
        /// <param name="nativeLibrariesByBasename">"sqlite3.dll" → bytes.</param>
        /// <param name="natives">Materializes native libraries to temp so the OS loader can map them.</param>
        public static SandboxPluginLoadContext ForBundle(
            IDictionary<string, byte[]> managedAssembliesByFullName,
            IDictionary<string, byte[]> nativeLibrariesByBasename,
            NativeLibraryMaterializer natives)
        {
            return new SandboxPluginLoadContext(
                managedResolver: an =>
                {
                    // 1) FullName hit
                    if (!string.IsNullOrEmpty(an.FullName) &&
                        managedAssembliesByFullName.TryGetValue(an.FullName!, out var exact))
                        return exact;

                    // 2) Fallback: simple name → best match
                    if (!string.IsNullOrEmpty(an.Name))
                    {
                        foreach (var kv in managedAssembliesByFullName)
                        {
                            var candidate = PeUtils.TryGetAssemblyName(kv.Value);
                            if (candidate?.Name != null &&
                                candidate.Name.Equals(an.Name, StringComparison.OrdinalIgnoreCase))
                                return kv.Value;
                        }
                    }

                    return null;
                },
                nativePathProvider: name => natives.GetOrMaterialize(name, nativeLibrariesByBasename));
        }

        /// <summary>
        /// The explicitly named type, or else the first type deriving from <paramref name="controlBaseType"/>.
        /// Tolerates assemblies where some types fail to load.
        /// </summary>
        public static Type? FindControlType(Assembly pluginAssembly, string? explicitTypeName, Type controlBaseType)
        {
            Type? controlType = null;
            if (!string.IsNullOrWhiteSpace(explicitTypeName))
                controlType = pluginAssembly.GetType(explicitTypeName!, throwOnError: false, ignoreCase: false);

            if (controlType == null)
            {
                try
                {
                    controlType = pluginAssembly.GetTypes()
                        .FirstOrDefault(t => controlBaseType.IsAssignableFrom(t));
                }
                catch (ReflectionTypeLoadException ex)
                {
                    controlType = ex.Types
                        .Where(t => t != null && controlBaseType.IsAssignableFrom(t))
                        .FirstOrDefault();
                }
            }

            return controlType;
        }

        // Dependencies of a plugin (NuGet packages) are the same bytes build after build. In a
        // process that can hot reload, plugin contexts are never unloaded, so loading them again
        // for every full reload would only pile up copies: each identical dependency is loaded
        // once and shared by every later plugin context (a context may return an assembly that
        // lives in another one). Keyed by identity and MVID, so a changed build is loaded anew.
        private static readonly ConcurrentDictionary<string, Assembly> s_sharedDependencies = new(StringComparer.Ordinal);

        private Assembly? OnResolveManaged(AssemblyLoadContext alc, AssemblyName name)
        {
            var bytes = _managedResolver(name);
            if (bytes == null) return null;

            var key = IsCollectible ? null : DependencyKey(bytes);
            if (key != null && s_sharedDependencies.TryGetValue(key, out var shared))
                return shared;

            using var ms = new MemoryStream(bytes, writable: false);
            var loaded = LoadFromStream(ms);
            if (key != null)
                s_sharedDependencies.TryAdd(key, loaded);
            return loaded;
        }

        private static string? DependencyKey(byte[] bytes)
        {
            try
            {
                using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(bytes, writable: false));
                var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
                var identity = metadata.GetAssemblyDefinition();
                var mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
                return metadata.GetString(identity.Name) + "/" + identity.Version + "/" + mvid.ToString("N");
            }
            catch
            {
                return null; // not a readable assembly: load it privately as before
            }
        }

        private IntPtr OnResolveUnmanaged(Assembly assembly, string unmanagedDllName)
        {
            // The provider throws DllNotFoundException when the bundle has no such library.
            var path = _nativePathProvider(unmanagedDllName);
            return File.Exists(path) ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
        }

        // No default probing — everything outside the default context comes via the Resolving events.
        protected override Assembly? Load(AssemblyName assemblyName) => null;

        public void Dispose()
        {
            // A context that can never be unloaded may still own dependencies later plugins share
            // (see s_sharedDependencies); those resolve their own references lazily through the
            // context they live in, so its handlers have to stay.
            if (!IsCollectible) return;

            Resolving -= OnResolveManaged;
            ResolvingUnmanagedDll -= OnResolveUnmanaged;
        }
    }

    /// <summary>
    /// Writes native libraries from a byte bundle into a content-addressed temp cache — the OS
    /// loader needs a real file. Honours <c>AC_WORK</c> so an AppContainer child stays inside
    /// the one folder it is allowed to write to.
    /// </summary>
    public sealed class NativeLibraryMaterializer
    {
        public const string CacheFolderName = "ac_native_cache";

        // name → absolute path in the temp cache
        private readonly Dictionary<string, string> _pathCache = new(StringComparer.OrdinalIgnoreCase);

        public string GetOrMaterialize(string nameOrBase, IDictionary<string, byte[]> nativeByBasename)
        {
            var fileName = nameOrBase.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? nameOrBase
                : nameOrBase + ".dll";

            if (_pathCache.TryGetValue(fileName, out var existing))
                return existing;

            if (!nativeByBasename.TryGetValue(fileName, out var bytes))
            {
                var kv = nativeByBasename.FirstOrDefault(k => string.Equals(k.Key, fileName, StringComparison.OrdinalIgnoreCase));
                if (kv.Key == null) throw new DllNotFoundException($"Native library '{fileName}' not found in bundle.");
                bytes = kv.Value;
            }

            var tempRoot = Path.Combine(GetTempRoot(), CacheFolderName);
            Directory.CreateDirectory(tempRoot);

            var hash = ComputeShortHash(bytes);
            var outPath = Path.Combine(tempRoot, $"{Path.GetFileNameWithoutExtension(fileName)}_{hash}.dll");

            if (!File.Exists(outPath))
            {
                var tmp = outPath + ".tmp_" + Guid.NewGuid().ToString("N");
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, outPath, overwrite: true);
            }

            _pathCache[fileName] = outPath;
            return outPath;
        }

        private static string GetTempRoot()
        {
            var ac = Environment.GetEnvironmentVariable("AC_WORK");
            var root = !string.IsNullOrWhiteSpace(ac) ? ac : Path.GetTempPath();

            var temp = Path.Combine(root, "temp");
            try { Directory.CreateDirectory(temp); } catch (Exception ex) { Debug.WriteLine($"[NativeLibraryMaterializer] {ex.Message}"); }
            return temp;
        }

        private static string ComputeShortHash(byte[] data)
        {
            var h = SHA256.HashData(data);
            return Convert.ToHexString(h.AsSpan(0, 6)); // 12 hex chars are plenty for a cache key
        }
    }
}
