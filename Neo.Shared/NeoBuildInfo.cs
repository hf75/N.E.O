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

        private static string ReadBuildTargetFramework()
        {
            var fromBuild = typeof(NeoBuildInfo).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "NeoTargetFramework")?.Value;

            // Only missing if someone builds Neo.Shared outside the repo's Directory.Build.props.
            return string.IsNullOrWhiteSpace(fromBuild) ? $"net{Environment.Version.Major}.0" : fromBuild;
        }
    }
}
