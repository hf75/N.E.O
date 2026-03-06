using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Neo.Agents.Core;

using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Packaging.Signing;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Resolver;
using NuGet.Versioning;

namespace Neo.Agents
{
    public class NuGetPackageLoaderAgent : AgentBase
    {
        public override string Name => "NuGetPackageLoaderAgent";

        protected override AgentMetadata CreateMetadata()
        {
            return new AgentMetadata
            {
                Name = this.Name,
                Description = "Lädt NuGet-Pakete und ignoriert strikte Windows-Versionsprüfungen.",
                InputParameters = new List<IInputParameter>
                {
                    new InputParameter<Dictionary<string, string>>("PackageNames", isRequired: true, "Paketname und Version."),
                    new InputParameter<string>("OutputDirectory", isRequired: true, "Zielverzeichnis."),
                    new InputParameter<string>("TargetFramework", isRequired: false, "Standard: auto-detected from runtime."),
                    new InputParameter<string>("RuntimeIdentifier", isRequired: false, "Standard: 'win-x64'.")
                },
                OutputParameters = new List<IOutputParameter>
                {
                    new OutputParameter<List<string>>("DllPaths", isAlwaysProvided: true),
                    new OutputParameter<Dictionary<string, string>>("PackageVersions", isAlwaysProvided: true)
                }
            };
        }

        public override void ValidateOptionsAndInputs()
        {
            if (GetInput<Dictionary<string, string>>("PackageNames")?.Count == 0) throw new ArgumentException("PackageNames leer.");
            if (string.IsNullOrWhiteSpace(GetInput<string>("OutputDirectory"))) throw new ArgumentException("OutputDirectory leer.");
        }

        public override async Task ExecuteAsync(CancellationToken? cancellationToken = null)
        {
            var ct = cancellationToken ?? CancellationToken.None;
            var rootPackagesInput = GetInput<Dictionary<string, string>>("PackageNames");
            var outputDirectory = GetInput<string>("OutputDirectory");

            var tfmString = GetInput<string>("TargetFramework");
            if (string.IsNullOrWhiteSpace(tfmString)) tfmString = $"net{Environment.Version.Major}.0-windows";

            var ridString = GetInput<string>("RuntimeIdentifier");
            if (string.IsNullOrWhiteSpace(ridString)) ridString = "win-x64";

            var packagesCacheDir = Path.Combine(outputDirectory, ".nuget_cache");
            ILogger nugetLogger = new AgentNuGetLogger(NullLogger.Instance);

            try
            {
                var result = await RestoreAndCopyAsync(
                    tfmString,
                    ridString,
                    rootPackagesInput,
                    packagesCacheDir,
                    outputDirectory,
                    ct);

                SetOutput("DllPaths", result.CopiedFiles);
                SetOutput("PackageVersions", result.InstalledPackages.ToDictionary(p => p.Id, p => p.Version.ToNormalizedString()));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"NuGet Error: {ex.Message}", ex);
            }
        }

        // --- Core Logic ---

        public sealed record RestoreResult(List<string> CopiedFiles, List<PackageIdentity> InstalledPackages);

        public static async Task<RestoreResult> RestoreAndCopyAsync(
            string targetFramework,
            string runtimeIdentifier,
            IDictionary<string, string> rootPackages,
            string packagesRootDirectory,
            string outputDirectory,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(targetFramework))
                throw new ArgumentException(nameof(targetFramework));
            if (string.IsNullOrWhiteSpace(runtimeIdentifier))
                throw new ArgumentException(nameof(runtimeIdentifier));
            if (rootPackages == null || rootPackages.Count == 0)
                throw new ArgumentException("rootPackages must not be empty.", nameof(rootPackages));
            if (string.IsNullOrWhiteSpace(packagesRootDirectory))
                throw new ArgumentException(nameof(packagesRootDirectory));
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new ArgumentException(nameof(outputDirectory));

            var framework = NuGetFramework.ParseFolder(targetFramework);
            var frameworkReducer = new FrameworkReducer();

            packagesRootDirectory = Path.GetFullPath(packagesRootDirectory);
            outputDirectory = Path.GetFullPath(outputDirectory);

            Directory.CreateDirectory(packagesRootDirectory);
            Directory.CreateDirectory(outputDirectory);

            var settings = Settings.LoadDefaultSettings(root: null);
            var packageSourceProvider = new PackageSourceProvider(settings);

            var sourceRepositoryProvider = new SourceRepositoryProvider(
                packageSourceProvider,
                Repository.Provider.GetCoreV3());

            var repositories = sourceRepositoryProvider.GetRepositories().ToList();
            if (repositories.Count == 0)
            {
                repositories.Add(Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json"));
            }

            using var cache = new SourceCacheContext();
            ILogger logger = NullLogger.Instance;

            // ---------------------------------------------------------------------
            // 1) Abhängigkeitsgraph aufbauen (inkl. transitiver Dependencies)
            // ---------------------------------------------------------------------
            var allPackages = new HashSet<SourcePackageDependencyInfo>(PackageIdentityComparer.Default);
            var actualRootIdentities = new List<PackageIdentity>();

            foreach (var kv in rootPackages)
            {
                var id = kv.Key;
                var versionString = kv.Value;
                NuGetVersion version;

                if (string.Equals(versionString, "default", StringComparison.OrdinalIgnoreCase))
                {
                    // "default" = neueste STABILE Version
                    version = await GetLatestVersionAsync(
                        id, repositories, cache, logger, cancellationToken);

                    if (version == null)
                    {
                        throw new InvalidOperationException(
                            $"Could not find any stable version for package '{id}' in the configured sources.");
                    }
                }
                else if (NuGetVersion.TryParse(versionString, out var fixedVersion))
                {
                    // feste Version (z. B. "3.119.1")
                    version = fixedVersion;
                }
                else if (VersionRange.TryParse(versionString, out var range))
                {
                    // Versionsbereich (z. B. ">= 3.0.0")
                    version = await GetLatestVersionAsync(
                        id, repositories, cache, logger, cancellationToken, range);

                    if (version == null)
                    {
                        throw new InvalidOperationException(
                            $"Could not find any version for package '{id}' that satisfies range '{versionString}'.");
                    }
                }
                else
                {
                    throw new ArgumentException(
                        $"Invalid version string '{versionString}' for package '{id}'.",
                        nameof(rootPackages));
                }

                var rootIdentity = new PackageIdentity(id, version);
                actualRootIdentities.Add(rootIdentity);

                await ListAllPackageDependenciesAsync(
                    rootIdentity,
                    repositories,
                    framework,
                    cache,
                    logger,
                    allPackages,
                    cancellationToken);
            }

            // ---------------------------------------------------------------------
            // 2) PackageResolver bestimmt konsistente Versionsmenge
            // ---------------------------------------------------------------------
            var resolverContext = new PackageResolverContext(
                dependencyBehavior: DependencyBehavior.Highest,   // Policy: bei Bedarf auf Highest ändern
                targetIds: rootPackages.Keys,
                requiredPackageIds: Enumerable.Empty<string>(),
                packagesConfig: Enumerable.Empty<PackageReference>(),
                preferredVersions: actualRootIdentities,
                availablePackages: allPackages,
                packageSources: repositories.Select(r => r.PackageSource),
                log: logger);

            var resolver = new PackageResolver();
            var resolvedIdentities = resolver.Resolve(resolverContext, cancellationToken).ToList();

            var packagesToInstall = resolvedIdentities
                .Select(id => allPackages.Single(p => PackageIdentityComparer.Default.Equals(p, id)))
                .ToList();

            // ---------------------------------------------------------------------
            // 3) Pakete herunterladen & extrahieren
            //    -> PackagePathResolver mit useSideBySidePaths: false
            //       ergibt Layout: <root>\<id>\<version>
            //       => keine Doppel-Struktur <id> + <id.version>
            // ---------------------------------------------------------------------
            var pathResolver = new PackagePathResolver(packagesRootDirectory, useSideBySidePaths: false);

            var extractionContext = new PackageExtractionContext(
                PackageSaveMode.Defaultv3,
                XmlDocFileSaveMode.Skip,
                ClientPolicyContext.GetClientPolicy(settings, logger),
                logger);

            var packageDownloadContext = new PackageDownloadContext(cache);

            foreach (var package in packagesToInstall)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var downloadResource = await package.Source
                    .GetResourceAsync<DownloadResource>(cancellationToken)
                    .ConfigureAwait(false);

                using var downloadResult = await downloadResource.GetDownloadResourceResultAsync(
                        package,
                        packageDownloadContext,
                        packagesRootDirectory,
                        logger,
                        cancellationToken)
                    .ConfigureAwait(false);

                var installPath = pathResolver.GetInstallPath(package);

                // Bereits im (globalen) Layout <root>\<id>\<version> vorhanden?
                if (downloadResult.Status == DownloadResourceResultStatus.AvailableWithoutStream &&
                    !string.IsNullOrEmpty(installPath) &&
                    Directory.Exists(installPath))
                {
                    continue;
                }

                await PackageExtractor.ExtractPackageAsync(
                        downloadResult.PackageSource,
                        downloadResult.PackageStream,
                        pathResolver,
                        extractionContext,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            // ---------------------------------------------------------------------
            // 4) Dateien aus lib + runtimes kopieren
            // ---------------------------------------------------------------------
            var copiedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var package in packagesToInstall)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var installPath = pathResolver.GetInstallPath(package);
                if (string.IsNullOrEmpty(installPath) || !Directory.Exists(installPath))
                    continue;

                using var packageReader = new PackageFolderReader(installPath);

                var libItems = packageReader.GetLibItems().ToList();
                var libFrameworks = libItems
                    .Where(g => g.Items != null && g.Items.Any())
                    .Select(g => g.TargetFramework)
                    .ToList();

                // NEU: robustere Auswahl des besten Frameworks für lib
                var bestLibFramework = SelectBestFrameworkForLib(
                    framework,
                    libFrameworks,
                    frameworkReducer,
                    runtimeIdentifier);

                if (bestLibFramework != null)
                {
                    var bestGroups = libItems
                        .Where(g => g.TargetFramework.Equals(bestLibFramework));


                    // In Schritt 4 (Lib Items):
                    foreach (var group in bestGroups)
                    {
                        foreach (var relativePath in group.Items)
                        {
                            var sourcePath = Path.Combine(
                                installPath,
                                relativePath.Replace('/', Path.DirectorySeparatorChar));

                            if (!File.Exists(sourcePath)) continue;

                            // Filter: Nur DLLs/Exes, ABER Resources (.resources.dll) beachten!
                            // IsManagedOrNativeBinary prüft auf .dll. Das passt.

                            if (!IsManagedOrNativeBinary(sourcePath)) continue;

                            // WICHTIG: Unterordner-Struktur beibehalten für Satellite Assemblies (z.B. "de/Lib.resources.dll")
                            // relativePath ist z.B. "lib/net462/de/MyLib.resources.dll"
                            // Wir wollen ab dem Framework-Ordner die Struktur behalten?
                            // Nein, meistens reicht es, zu schauen, ob es eine Resource ist.

                            string fileName = Path.GetFileName(sourcePath);
                            string destPath;

                            // Einfache Heuristik für Satellite Assemblies (liegen meist in Ordnern wie de, fr, es)
                            var dirName = Path.GetFileName(Path.GetDirectoryName(sourcePath));
                            if (fileName.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase) && dirName != null && dirName.Length >= 2 && dirName.Length <= 5)
                            {
                                // Es ist eine Resource! Unterordner erstellen (z.B. output/de/...)
                                var cultureDir = Path.Combine(outputDirectory, dirName);
                                Directory.CreateDirectory(cultureDir);
                                destPath = Path.Combine(cultureDir, fileName);
                            }
                            else
                            {
                                // Normale DLL -> flach ins Root
                                destPath = Path.Combine(outputDirectory, fileName);
                            }

                            // Kopieren...
                            if (!copiedFiles.Contains(destPath))
                            {
                                File.Copy(sourcePath, destPath, overwrite: true);
                                copiedFiles.Add(destPath);
                            }
                        }
                    }

                }

                // ---------------------------------------------------------------------
                // 4b/4c) Runtimes verarbeiten (runtimes/<RID>/lib/<tfm> und runtimes/<RID>/native)
                // ---------------------------------------------------------------------
                var runtimesRoot = Path.Combine(installPath, "runtimes");
                if (Directory.Exists(runtimesRoot))
                {
                    // 1. RID-Kandidaten holen (z.B. "win-x64", "win", "any")
                    // Die Liste ist nach Priorität sortiert (Spezifisch -> Generisch)
                    var ridCandidates = GetRidCandidates(runtimeIdentifier);

                    foreach (var rid in ridCandidates)
                    {
                        var ridDir = Path.Combine(runtimesRoot, rid);
                        if (!Directory.Exists(ridDir)) continue;

                        // --- A) Managed Libs im Runtime-Ordner (runtimes/<RID>/lib/<tfm>) ---
                        // Beispiel: System.Drawing.Common hat hier unix-spezifische DLLs
                        var ridLibRoot = Path.Combine(ridDir, "lib");
                        if (Directory.Exists(ridLibRoot))
                        {
                            var tfmDirs = Directory.GetDirectories(ridLibRoot);
                            var candidateFrameworks = new List<(NuGetFramework Framework, string Dir)>();

                            foreach (var dir in tfmDirs)
                            {
                                var tfmName = Path.GetFileName(dir);
                                var fw = NuGetFramework.ParseFolder(tfmName);
                                if (!fw.IsUnsupported)
                                {
                                    candidateFrameworks.Add((fw, dir));
                                }
                            }

                            if (candidateFrameworks.Count > 0)
                            {
                                // Hier nutzen wir den Standard-Reducer, da Runtime-Libs meist exakt passen müssen
                                var nearestRuntimeFramework = frameworkReducer.GetNearest(
                                    framework,
                                    candidateFrameworks.Select(c => c.Framework));

                                if (nearestRuntimeFramework != null)
                                {
                                    var bestDir = candidateFrameworks
                                        .First(c => c.Framework.Equals(nearestRuntimeFramework))
                                        .Dir;

                                    foreach (var file in Directory.EnumerateFiles(bestDir, "*.*", SearchOption.AllDirectories))
                                    {
                                        if (!IsManagedOrNativeBinary(file)) continue;

                                        var fileName = Path.GetFileName(file);
                                        var destPath = Path.Combine(outputDirectory, fileName);

                                        // WICHTIG: Prioritäts-Check!
                                        // Da wir mit dem spezifischsten RID angefangen haben (z.B. win-x64),
                                        // darf ein späterer, generischer RID (z.B. win) die Datei NICHT überschreiben.
                                        if (!copiedFiles.Contains(destPath))
                                        {
                                            File.Copy(file, destPath, overwrite: true);
                                            copiedFiles.Add(destPath);
                                        }
                                    }
                                }
                            }
                        }

                        // --- B) Native Libs (runtimes/<RID>/native) ---
                        // Beispiel: WebView2Loader.dll, libSkiaSharp.so, sqlite3.dll
                        var nativeRoot = Path.Combine(ridDir, "native");
                        if (Directory.Exists(nativeRoot))
                        {
                            foreach (var file in Directory.EnumerateFiles(nativeRoot, "*.*", SearchOption.AllDirectories))
                            {
                                if (!IsNativeLibrary(file)) continue;

                                var fileName = Path.GetFileName(file);
                                var destPath = Path.Combine(outputDirectory, fileName);

                                // Auch hier: Wer zuerst kommt (spezifischer RID), gewinnt.
                                if (!copiedFiles.Contains(destPath))
                                {
                                    File.Copy(file, destPath, overwrite: true);
                                    copiedFiles.Add(destPath);
                                }
                            }
                        }
                    }
                }
            }

            return new RestoreResult(copiedFiles.ToList(), resolvedIdentities);
        }

        /// <summary>
        /// Ermittelt Kandidaten für RID-Ordner basierend auf dem gewünschten RuntimeIdentifier.
        /// Simuliert vereinfacht den NuGet RID-Graphen.
        /// </summary>
        private static List<string> GetRidCandidates(string runtimeIdentifier)
        {
            var candidates = new List<string>();

            // 1. Exakter Treffer (z. B. "win-x64", "ubuntu.20.04-x64", "osx-arm64")
            candidates.Add(runtimeIdentifier);

            var parts = runtimeIdentifier.Split('-');
            var arch = parts.Length > 1 ? parts.Last() : null; // z. B. "x64", "arm64"

            bool isWindows = runtimeIdentifier.StartsWith("win", StringComparison.OrdinalIgnoreCase);
            bool isMac = runtimeIdentifier.StartsWith("osx", StringComparison.OrdinalIgnoreCase) ||
                         runtimeIdentifier.StartsWith("macos", StringComparison.OrdinalIgnoreCase);

            if (isWindows)
            {
                // Fallback: win10-x64 -> win-x64
                if (!string.IsNullOrEmpty(arch) && !runtimeIdentifier.Equals($"win-{arch}", StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add($"win-{arch}");
                }
                // Fallback: win
                candidates.Add("win");
            }
            else if (isMac)
            {
                // Fallback: osx.10.12-x64 -> osx-x64
                if (!string.IsNullOrEmpty(arch) && !runtimeIdentifier.Equals($"osx-{arch}", StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add($"osx-{arch}");
                }
                // Fallback: osx
                candidates.Add("osx");
                // Fallback: unix (viele Core-Libs legen hier Shared-Code ab)
                candidates.Add("unix");
            }
            else // Linux / Andere
            {
                // Fallback: ubuntu.20.04-x64 -> linux-x64
                // Wir nehmen an, alles was nicht win/mac ist, ist linux-artig
                if (!string.IsNullOrEmpty(arch) && !runtimeIdentifier.StartsWith("linux", StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add($"linux-{arch}");
                }

                candidates.Add("linux");
                candidates.Add("unix");
            }

            // Duplikate entfernen und Reihenfolge beibehalten
            return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool IsManagedOrNativeBinary(string path)
        {
            var ext = Path.GetExtension(path);
            return ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".so", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".dylib", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNativeLibrary(string path)
        {
            var name = Path.GetFileName(path);
            // Windows
            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return true;
            // Mac
            if (name.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase)) return true;
            // Linux / Unix (auch .so.1, .so.1.0 etc.)
            if (name.Contains(".so", StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        private static async Task ListAllPackageDependenciesAsync(
            PackageIdentity package,
            IEnumerable<SourceRepository> repositories,
            NuGetFramework framework,
            SourceCacheContext cache,
            ILogger logger,
            ISet<SourcePackageDependencyInfo> dependencies,
            CancellationToken cancellationToken)
        {
            // Schon vorhanden?
            if (dependencies.Any(p => PackageIdentityComparer.Default.Equals(p, package)))
                return;

            var frameworkReducer = new FrameworkReducer();

            foreach (var repository in repositories)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var findResource = await repository
                    .GetResourceAsync<FindPackageByIdResource>(cancellationToken)
                    .ConfigureAwait(false);

                if (findResource == null)
                    continue;

                var packageInfo = await findResource.GetDependencyInfoAsync(
                        package.Id,
                        package.Version,
                        cache,
                        logger,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (packageInfo == null)
                    continue;

                var groups = packageInfo.DependencyGroups?.ToList()
                            ?? new List<PackageDependencyGroup>();

                // passenden Dependency-Block zum Ziel-Framework auswählen
                var candidateFrameworks = groups
                    .Select(g => g.TargetFramework)
                    .Where(fw => fw != null && !fw.IsUnsupported)
                    .ToList();

                PackageDependencyGroup? selectedGroup = null;

                if (candidateFrameworks.Count > 0)
                {
                    var nearest = frameworkReducer.GetNearest(framework, candidateFrameworks);

                    if (nearest != null)
                    {
                        selectedGroup = groups.FirstOrDefault(g => g.TargetFramework.Equals(nearest));
                    }

                    // Fallback: AnyFramework oder erster Block
                    if (selectedGroup == null)
                    {
                        selectedGroup = groups.FirstOrDefault(g => g.TargetFramework.Equals(NuGetFramework.AnyFramework))
                                        ?? groups.FirstOrDefault();
                    }
                }
                else
                {
                    // kein Framework-spezifischer Block -> AnyFramework oder gar keine Dependencies
                    selectedGroup = groups.FirstOrDefault(g => g.TargetFramework.Equals(NuGetFramework.AnyFramework))
                                    ?? groups.FirstOrDefault();
                }

                var deps = selectedGroup?.Packages ?? Enumerable.Empty<PackageDependency>();

                var sourceDepInfo = new SourcePackageDependencyInfo(
                    package.Id,
                    package.Version,
                    deps,
                    listed: true,
                    source: repository);

                if (!dependencies.Add(sourceDepInfo))
                {
                    // schon vorhanden
                    return;
                }

                // Jetzt rekursiv alle Dependencies auflösen
                foreach (var dep in deps)
                {
                    var depVersion = await ResolveDependencyVersionAsync(
                            dep,
                            repositories,
                            cache,
                            logger,
                            cancellationToken)
                        .ConfigureAwait(false);

                    var depIdentity = new PackageIdentity(dep.Id, depVersion);

                    await ListAllPackageDependenciesAsync(
                            depIdentity,
                            repositories,
                            framework,
                            cache,
                            logger,
                            dependencies,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                // Paket in diesem Repository gefunden -> andere Repos nicht mehr nötig
                break;
            }
        }

        private static NuGetFramework? SelectBestFrameworkForLib(
            NuGetFramework target,
            IEnumerable<NuGetFramework> candidates,
            FrameworkReducer reducer,
            string runtimeIdentifier)
        {
            var list = candidates
                .Where(f => f != null && !f.IsUnsupported)
                .ToList();

            if (list.Count == 0)
                return null;

            // 1) Exakter Treffer (Performance)
            if (list.Any(f => f.Equals(target)))
                return target;

            // Prüfen, ob wir auf Windows laufen (basierend auf RID oder Target Platform)
            bool isWindows = runtimeIdentifier.StartsWith("win", StringComparison.OrdinalIgnoreCase) ||
                             (target.HasPlatform && string.Equals(target.Platform, "Windows", StringComparison.OrdinalIgnoreCase));

            // ---------------------------------------------------------------------
            // SPEZIAL-LOGIK FÜR DESKTOP-PACKAGES (wie WebView2)
            // ---------------------------------------------------------------------
            // Szenario: Wir sind .NET 9 (Core), das Paket hat "net462" (Framework) und "netstandard2.0".
            // NuGet würde standardmäßig "netstandard2.0" wählen. Das enthält aber oft keine UI-DLLs (WPF/WinForms).
            // Wenn wir auf Windows sind, wollen wir in diesem Fall lieber "net462" erzwingen (Asset Target Fallback).
            if (isWindows && target.Framework == ".NETCoreApp")
            {
                // Haben wir eine echte .NET Core Implementierung (z.B. net6.0, netcoreapp3.1)?
                bool hasNetCore = list.Any(f => f.Framework == ".NETCoreApp");

                // Wenn NEIN, aber wir haben .NET Framework Kandidaten (net4xx)...
                if (!hasNetCore)
                {
                    var bestNetFramework = list
                        .Where(f => f.Framework == ".NETFramework") // z.B. net462
                        .OrderByDescending(f => f.Version)
                        .FirstOrDefault();

                    // ...dann nimm das .NET Framework (auch wenn netstandard existiert!)
                    if (bestNetFramework != null)
                    {
                        return bestNetFramework;
                    }
                }
            }

            // 2) Standard NuGet-Logik (GetNearest)
            var nearest = reducer.GetNearest(target, list);

            // Wenn NuGet etwas gefunden hat, nehmen wir das (außer unsere Speziallogik oben hat schon gegriffen)
            if (nearest != null)
                return nearest;

            // 3) Asset Target Fallback (Manuell)
            // Falls GetNearest 'null' liefert (weil .NET Core -> .NET Framework standardmäßig oft blockiert wird),
            // wir aber auf Windows sind, erlauben wir den Fallback auf das höchste .NET Framework.
            if (isWindows && target.Framework == ".NETCoreApp")
            {
                var fallback = list
                    .Where(f => f.Framework == ".NETFramework")
                    .OrderByDescending(f => f.Version)
                    .FirstOrDefault();

                if (fallback != null)
                    return fallback;
            }

            // 4) Letzter Ausweg: Einfach das höchste verfügbare Framework nehmen
            return list
                .OrderByDescending(f => f.Version)
                .FirstOrDefault();
        }

        // --- DEPENDENCY RESOLUTION ---

        private static async Task<NuGetVersion> ResolveDependencyVersionAsync(
            PackageDependency dependency,
            IEnumerable<SourceRepository> repositories,
            SourceCacheContext cache,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var range = dependency.VersionRange ?? VersionRange.All;
            var allVersions = new List<NuGetVersion>();

            foreach (var repository in repositories)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var findResource = await repository
                    .GetResourceAsync<FindPackageByIdResource>(cancellationToken)
                    .ConfigureAwait(false);

                if (findResource == null)
                    continue;

                var versions = await findResource.GetAllVersionsAsync(
                        dependency.Id,
                        cache,
                        logger,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (versions != null)
                {
                    allVersions.AddRange(versions);
                }
            }

            if (allVersions.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Could not find any version for dependency '{dependency.Id}'.");
            }

            var bestVersion = range.FindBestMatch(allVersions);
            if (bestVersion == null)
            {
                throw new InvalidOperationException(
                    $"No version of '{dependency.Id}' satisfies version range '{range.ToNormalizedString()}'.");
            }

            return bestVersion;
        }

        // --- DER FIX: Tolerante Framework-Auswahl ---

        /// <summary>
        /// Findet das beste Framework und ignoriert dabei Platform-Versionen (z.B. windows10.0 vs windows7.0).
        /// </summary>
        private static NuGetFramework GetCompatibleFramework(NuGetFramework target, IEnumerable<NuGetFramework> candidates)
        {
            var reducer = new FrameworkReducer();

            // 1. Offizieller Weg (funktioniert, wenn Versionen passen)
            var nearest = reducer.GetNearest(target, candidates);
            if (nearest != null) return nearest;

            // 2. Toleranter Weg: Wir filtern Kandidaten manuell
            // Wir suchen Kandidaten, die zur gleichen .NET Familie gehören (Core/Standard)
            // UND die gleiche Plattform haben (oder gar keine), aber wir ignorieren die Plattform-VERSION.

            var compatibleCandidates = candidates.Where(candidate =>
            {
                // Framework-Identifier muss passen (.NETCoreApp vs .NETCoreApp)
                if (!string.Equals(candidate.Framework, target.Framework, StringComparison.OrdinalIgnoreCase))
                    return false;

                // Version des Kandidaten muss <= Target Version sein (wir können net8 lib in net9 app laden)
                if (candidate.Version > target.Version)
                    return false;

                // Plattform-Check (Das ist der kritische Teil!)
                if (target.HasPlatform)
                {
                    // Wenn Target Windows ist, muss Kandidat auch Windows (oder plattformneutral) sein
                    if (candidate.HasPlatform)
                    {
                        if (!string.Equals(candidate.Platform, target.Platform, StringComparison.OrdinalIgnoreCase))
                            return false;

                        // HIER IST DER TRICK: Wir prüfen NICHT candidate.PlatformVersion <= target.PlatformVersion
                        // Wir erlauben einfach alles, solange es "Windows" ist.
                    }
                }

                return true;
            }).ToList();

            // Nimm den Kandidaten mit der höchsten Version
            return compatibleCandidates.OrderByDescending(c => c.Version).FirstOrDefault()!;
        }

        // --- Helpers ---

        private static async Task<NuGetVersion> GetLatestVersionAsync(
            string packageId,
            IEnumerable<SourceRepository> repositories,
            SourceCacheContext cache,
            ILogger logger,
            CancellationToken cancellationToken,
            VersionRange? versionRange = null)
        {
            NuGetVersion? bestVersion = null;
            var includePrerelease = false;

            foreach (var repo in repositories)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var metadataResource = await repo
                    .GetResourceAsync<PackageMetadataResource>(cancellationToken)
                    .ConfigureAwait(false);

                if (metadataResource == null)
                    continue;

                var metadata = await metadataResource.GetMetadataAsync(
                        packageId,
                        includePrerelease,
                        includeUnlisted: false,
                        cache,
                        logger,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (metadata == null)
                    continue;

                foreach (var item in metadata)
                {
                    var v = item.Identity.Version;
                    if (versionRange != null && !versionRange.Satisfies(v))
                        continue;

                    if (bestVersion == null || v > bestVersion)
                    {
                        bestVersion = v;
                    }
                }
            }

            return bestVersion!;
        }

        private static void CopyFile(string root, string relativePath, string targetDir, HashSet<string> tracker)
        {
            var source = Path.GetFullPath(Path.Combine(root, relativePath));
            CopyFileDirect(source, targetDir, tracker);
        }

        private static void CopyFileDirect(string sourcePath, string targetDir, HashSet<string> tracker)
        {
            if (File.Exists(sourcePath))
            {
                var dest = Path.Combine(targetDir, Path.GetFileName(sourcePath));
                File.Copy(sourcePath, dest, overwrite: true);
                tracker.Add(dest);
            }
        }

        private static bool IsBinary(string path)
        {
            var ext = Path.GetExtension(path);
            return ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNativeBinary(string path)
        {
            var ext = Path.GetExtension(path);
            return ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".so", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".dylib", StringComparison.OrdinalIgnoreCase);
        }

        private class AgentNuGetLogger : LoggerBase
        {
            private readonly ILogger _inner;
            public AgentNuGetLogger(ILogger inner) { _inner = inner; }
            public override void Log(ILogMessage message) { }
            public override Task LogAsync(ILogMessage message) { Log(message); return Task.CompletedTask; }
        }
    }
}
