using System;

namespace Neo.PluginWindowWPF
{
    /// <summary>
    /// What an executable head may customise about the shared plugin window.
    /// Everything else — IPC loop, plugin loading, hot reload, web bridge, the MCP
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
        /// What the window answers when asked for its code. WPF has no visual-tree code extraction,
        /// so a head that works on code (Smart Edit) supplies the last source it built or received.
        /// </summary>
        public Func<string?>? LastCompiledCodeProvider { get; init; }

        /// <summary>
        /// The source of the build the MCP server just sent (the desktop hosts send none). The window
        /// only runs builds; a head that works on code (Smart Edit) keeps it.
        /// </summary>
        public Action<string>? SourceCodeReceived { get; init; }
    }

    /// <summary>Entry point shared by all WPF plugin window executables.</summary>
    public static class PluginHost
    {
        public static PluginHostOptions Options { get; private set; } = new();

        /// <summary>Runs the plugin window application. Must be called from an STA thread.</summary>
        public static int Run(PluginHostOptions? options = null)
        {
            Options = options ?? new PluginHostOptions();
            // Windows-1252 and friends: tools read such files, and .NET knows them only after this.
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            return new App().Run();
        }
    }
}
