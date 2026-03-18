using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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

namespace Neo.App
{
    public static class NuGetBinaryCopier
    {
        public sealed record Result(
            IReadOnlyList<string> CopiedFiles,
            string PackagesRoot,
            string OutputDirectory);

        public static async Task<Result> RestoreAndCopyAsync(
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

            // 1) Abhängigkeitsgraph aufbauen
            var allPackages = new HashSet<SourcePackageDependencyInfo>(PackageIdentityComparer.Default);

            // Liste für die tatsächlich ermittelten Root-Identities (Version "default" wird hier aufgelöst)
            var actualRootIdentities = new List<PackageIdentity>();

            foreach (var kv in rootPackages)
            {
                var id = kv.Key;
                var versionString = kv.Value;
                NuGetVersion version;

                // NEU: Check auf "default" (case-insensitive)
                if (string.Equals(versionString, "default", StringComparison.OrdinalIgnoreCase))
                {
                    // Neueste Version suchen
                    version = await GetLatestVersionAsync(id, repositories, cache, logger, cancellationToken);
                    if (version == null)
                    {
                        throw new InvalidOperationException($"Could not find any version for package '{id}' in the configured sources.");
                    }
                }
                else
                {
                    // Feste Version parsen
                    version = NuGetVersion.Parse(versionString);
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

            // 2) Mit PackageResolver die effektive Versionsmenge bestimmen
            // WICHTIG: Hier nutzen wir jetzt 'actualRootIdentities' statt 'rootPackages' direkt zu parsen.
            var resolverContext = new PackageResolverContext(
                dependencyBehavior: DependencyBehavior.Lowest,
                targetIds: rootPackages.Keys,
                requiredPackageIds: Enumerable.Empty<string>(),
                packagesConfig: Enumerable.Empty<PackageReference>(),
                preferredVersions: actualRootIdentities, // Hier die aufgelösten Versionen übergeben
                availablePackages: allPackages,
                packageSources: repositories.Select(r => r.PackageSource),
                log: logger);

            var resolver = new PackageResolver();
            var resolvedIdentities = resolver
                .Resolve(resolverContext, cancellationToken)
                .ToList();

            var packagesToInstall = resolvedIdentities
                .Select(id => allPackages.Single(p => PackageIdentityComparer.Default.Equals(p, id)))
                .ToList();

            // 3) Pakete herunterladen und extrahieren
            var pathResolver = new PackagePathResolver(packagesRootDirectory, useSideBySidePaths: true);
            var extractionContext = new PackageExtractionContext(
                PackageSaveMode.Defaultv3,
                XmlDocFileSaveMode.Skip,
                ClientPolicyContext.GetClientPolicy(settings, logger),
                logger);

            foreach (var package in packagesToInstall)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var downloadResource = await package.Source
                    .GetResourceAsync<DownloadResource>(cancellationToken)
                    .ConfigureAwait(false);

                using var downloadResult = await downloadResource.GetDownloadResourceResultAsync(
                    package,
                    new PackageDownloadContext(cache),
                    packagesRootDirectory,
                    logger,
                    cancellationToken).ConfigureAwait(false);

                if (downloadResult.Status == DownloadResourceResultStatus.AvailableWithoutStream &&
                    Directory.Exists(pathResolver.GetInstallPath(package)))
                {
                    continue;
                }

                await PackageExtractor.ExtractPackageAsync(
                    downloadResult.PackageSource,
                    downloadResult.PackageStream,
                    pathResolver,
                    extractionContext,
                    cancellationToken).ConfigureAwait(false);
            }

            // 4) Dateien kopieren (unverändert)
            var copiedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var package in packagesToInstall)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var installPath = pathResolver.GetInstallPath(package);
                if (installPath == null || !Directory.Exists(installPath))
                    continue;

                using var packageReader = new PackageFolderReader(installPath);

                var libItems = packageReader.GetLibItems().ToList();
                var libFrameworks = libItems
                    .Where(g => g.Items != null && g.Items.Any())
                    .Select(g => g.TargetFramework)
                    .ToList();

                NuGetFramework? nearestLibFramework = null;
                if (libFrameworks.Count > 0)
                {
                    nearestLibFramework = frameworkReducer.GetNearest(framework, libFrameworks);
                }

                if (nearestLibFramework != null)
                {
                    var bestGroups = libItems
                        .Where(g => g.TargetFramework.Equals(nearestLibFramework));

                    foreach (var group in bestGroups)
                    {
                        foreach (var relativePath in group.Items)
                        {
                            var sourcePath = Path.Combine(installPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                            if (!File.Exists(sourcePath)) continue;
                            if (!IsManagedOrNativeBinary(sourcePath)) continue;

                            var destPath = Path.Combine(outputDirectory, Path.GetFileName(sourcePath));
                            Directory.CreateDirectory(outputDirectory); // Sicherstellen, dass Ziel existiert
                            File.Copy(sourcePath, destPath, overwrite: true);
                            copiedFiles.Add(destPath);
                        }
                    }
                }

                var runtimesRoot = Path.Combine(installPath, "runtimes");
                var ridRoot = Path.Combine(runtimesRoot, runtimeIdentifier);

                if (Directory.Exists(ridRoot))
                {
                    var ridLibRoot = Path.Combine(ridRoot, "lib");
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
                            var nearestRuntimeFramework = frameworkReducer.GetNearest(
                                framework,
                                candidateFrameworks.Select(c => c.Framework));

                            var bestDir = candidateFrameworks
                                .FirstOrDefault(c => c.Framework.Equals(nearestRuntimeFramework))
                                .Dir;

                            if (!string.IsNullOrEmpty(bestDir) && Directory.Exists(bestDir))
                            {
                                foreach (var file in Directory.EnumerateFiles(bestDir, "*.*", SearchOption.AllDirectories))
                                {
                                    if (!IsManagedOrNativeBinary(file)) continue;
                                    var destPath = Path.Combine(outputDirectory, Path.GetFileName(file));
                                    File.Copy(file, destPath, overwrite: true);
                                    copiedFiles.Add(destPath);
                                }
                            }
                        }
                    }

                    var nativeRoot = Path.Combine(ridRoot, "native");
                    if (Directory.Exists(nativeRoot))
                    {
                        foreach (var file in Directory.EnumerateFiles(nativeRoot, "*.*", SearchOption.AllDirectories))
                        {
                            if (!IsNativeLibrary(file)) continue;
                            var destPath = Path.Combine(outputDirectory, Path.GetFileName(file));
                            File.Copy(file, destPath, overwrite: true);
                            copiedFiles.Add(destPath);
                        }
                    }
                }
            }

            return new Result(copiedFiles.ToList(), packagesRootDirectory, outputDirectory);
        }

        /// <summary>
        /// Sucht die neueste STABILE Version eines Pakets über alle Repositories hinweg.
        /// </summary>
        private static async Task<NuGetVersion> GetLatestVersionAsync(
            string packageId,
            IEnumerable<SourceRepository> repositories,
            SourceCacheContext cache,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            NuGetVersion? bestVersion = null;

            foreach (var repo in repositories)
            {
                // PackageMetadataResource ist besser als FindPackageByIdResource, 
                // da es "IsListed" und "IsPrerelease" sauberer handhabt.
                var metadataResource = await repo.GetResourceAsync<PackageMetadataResource>(cancellationToken);
                if (metadataResource == null) continue;

                var metadata = await metadataResource.GetMetadataAsync(
                    packageId,
                    includePrerelease: false, // "default" = nur Stable Versions
                    includeUnlisted: false,
                    cache,
                    logger,
                    cancellationToken);

                if (metadata == null) continue;

                var maxInRepo = metadata
                    .Select(m => m.Identity.Version)
                    .Max();

                if (maxInRepo != null)
                {
                    if (bestVersion == null || maxInRepo > bestVersion)
                    {
                        bestVersion = maxInRepo;
                    }
                }
            }

            return bestVersion!;
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
            if (dependencies.Any(p => PackageIdentityComparer.Default.Equals(p, package)))
                return;

            foreach (var repository in repositories)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var dependencyInfoResource = await repository
                    .GetResourceAsync<DependencyInfoResource>(cancellationToken)
                    .ConfigureAwait(false);

                if (dependencyInfoResource == null) continue;

                var dependencyInfo = await dependencyInfoResource.ResolvePackage(
                    package,
                    framework,
                    cache,
                    logger,
                    cancellationToken).ConfigureAwait(false);

                if (dependencyInfo == null)
                    continue;

                if (dependencies.Add(dependencyInfo))
                {
                    foreach (var dep in dependencyInfo.Dependencies)
                    {
                        var depIdentity = new PackageIdentity(dep.Id, dep.VersionRange.MinVersion);
                        await ListAllPackageDependenciesAsync(
                            depIdentity,
                            repositories,
                            framework,
                            cache,
                            logger,
                            dependencies,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                break;
            }
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
            var ext = Path.GetExtension(path);
            return ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".so", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".dylib", StringComparison.OrdinalIgnoreCase);
        }
    }
}
