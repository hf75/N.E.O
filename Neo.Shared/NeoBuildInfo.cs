using System.Reflection;

namespace Neo.Shared
{
    /// <summary>
    /// Which .NET N.E.O. was built for, and which one it is running on. The two can differ: every
    /// executable rolls forward to a newer major when the one it was built for is not installed.
    ///
    /// <para>Use <see cref="TargetFramework"/> for anything that names a <b>build output folder</b>
    /// (<c>bin/Debug/net10.0</c>) and <see cref="RuntimeTargetFramework"/> for anything that has to
    /// match the <b>running</b> runtime (which NuGet assets to pick for generated code, what to
    /// tell the user or the model). Never spell a framework moniker out in code.</para>
    /// </summary>
    public static class NeoBuildInfo
    {
        /// <summary>
        /// The target framework of this build, e.g. <c>net10.0</c>. Comes from the
        /// <c>NeoTargetFramework</c> property in Directory.Build.props, which the build stamps
        /// into every assembly as assembly metadata.
        /// </summary>
        public static string TargetFramework { get; } = ReadBuildTargetFramework();

        /// <summary>The Windows-specific flavour of <see cref="TargetFramework"/>, e.g. <c>net10.0-windows</c>.</summary>
        public static string TargetFrameworkWindows => TargetFramework + "-windows";

        /// <summary>Major version of the runtime this process actually runs on.</summary>
        public static int RuntimeMajor => Environment.Version.Major;

        /// <summary>Framework moniker of the running runtime, e.g. <c>net11.0</c> after a roll-forward.</summary>
        public static string RuntimeTargetFramework => $"net{RuntimeMajor}.0";

        /// <summary>
        /// The Avalonia version N.E.O. ships, e.g. <c>12.1.2</c> — the <c>NeoAvaloniaVersion</c>
        /// property in Directory.Build.props. Generated code is compiled against and runs on
        /// exactly these assemblies, so this is the version to name wherever one is needed.
        /// </summary>
        public static string AvaloniaVersion { get; } = ReadBuildMetadata("NeoAvaloniaVersion") ?? string.Empty;

        /// <summary>
        /// Build configurations to probe when a host looks for a sibling project's output at dev
        /// time (<c>../Neo.PluginWindowAvalonia/bin/&lt;Configuration&gt;/&lt;tfm&gt;</c>). The
        /// configuration this process was built with comes first: a Release server must start the
        /// Release child and compile against its assemblies, even when a (possibly stale) Debug
        /// build lies next to it.
        /// </summary>
        public static IReadOnlyList<string> DevBuildConfigurations { get; } = OrderConfigurations(AppContext.BaseDirectory);

        /// <summary>See <see cref="DevBuildConfigurations"/>; <paramref name="baseDirectory"/> is <c>…/bin/&lt;Configuration&gt;/&lt;tfm&gt;/</c>.</summary>
        public static IReadOnlyList<string> OrderConfigurations(string baseDirectory)
        {
            var tfmDir = baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var own = Path.GetFileName(Path.GetDirectoryName(tfmDir));

            return string.Equals(own, "Release", StringComparison.OrdinalIgnoreCase)
                ? ["Release", "Debug"]
                : ["Debug", "Release"];
        }

        private static string ReadBuildTargetFramework()
        {
            var fromBuild = ReadBuildMetadata("NeoTargetFramework");

            // Only missing if someone builds Neo.Shared outside the repo's Directory.Build.props.
            return string.IsNullOrWhiteSpace(fromBuild) ? $"net{Environment.Version.Major}.0" : fromBuild;
        }

        private static string? ReadBuildMetadata(string key)
            => typeof(NeoBuildInfo).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == key)?.Value;
    }
}
