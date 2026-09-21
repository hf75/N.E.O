using System;

namespace Neo.PluginWindowWPF
{
    /// <summary>
    /// What an executable head may customise about the shared plugin window.
    /// Everything else — IPC loop, plugin loading, designer mode, web bridge, the MCP
    /// inspection handlers — is identical for every head and lives in this library.
    /// </summary>
    public sealed class PluginHostOptions
    {
        /// <summary>Window title used in <c>--standalone</c> mode (embedded mode has no title bar).</summary>
        public string StandaloneWindowTitle { get; init; } = "N.E.O. — Live Preview (WPF)";

        /// <summary>
        /// Show the "Waiting for code…" placeholder while no control is loaded. The WPF desktop
        /// host renders its own placeholder over the embedded child, so its head turns this off.
        /// </summary>
        public bool ShowWaitOverlay { get; init; } = true;

        /// <summary>
        /// Called once, right after the <see cref="MainWindow"/> was constructed and before it is
        /// shown. Heads use this to attach their own features (the MCP head adds Smart Edit).
        /// </summary>
        public Action<MainWindow>? MainWindowCreated { get; init; }

        /// <summary>
        /// Source for <c>extract_code</c>. WPF has no visual-tree code extraction, so a head that
        /// compiles code itself (Smart Edit) supplies the last source it built.
        /// </summary>
        public Func<string?>? LastCompiledCodeProvider { get; init; }
    }

    /// <summary>Entry point shared by all WPF plugin window executables.</summary>
    public static class PluginHost
    {
        public static PluginHostOptions Options { get; private set; } = new();

        /// <summary>Runs the plugin window application. Must be called from an STA thread.</summary>
        public static int Run(PluginHostOptions? options = null)
        {
            Options = options ?? new PluginHostOptions();
            return new App().Run();
        }
    }
}
