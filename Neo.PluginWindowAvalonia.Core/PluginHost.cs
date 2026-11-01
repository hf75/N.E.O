using System;

using Avalonia;

namespace Neo.PluginWindowAvalonia
{
    /// <summary>
    /// What an executable head may customise about the shared plugin window.
    /// Everything else — IPC loop, plugin loading, hot reload, Live-MCP, web bridge —
    /// is identical for every head and lives in this library.
    /// </summary>
    public sealed class PluginHostOptions
    {
        /// <summary>Window title used in <c>--standalone</c> mode (embedded mode has no title bar).</summary>
        public string StandaloneWindowTitle { get; init; } = "N.E.O. — Live Preview";

        /// <summary>
        /// The host on the other end of the pipe is Neo.McpServer: announce the Live-MCP manifest
        /// after every load and forward <c>Neo.App.Ai.Trigger</c> as <c>AppEvent</c> frames (which
        /// the server relays to Claude). The desktop hosts understand neither, so their head leaves
        /// this off and the child stays as quiet on the pipe as it always was.
        /// </summary>
        public bool EnableMcpHostFeatures { get; init; }

        /// <summary>
        /// Called once, right after the <see cref="MainWindow"/> was constructed and before it is
        /// shown. Heads use this to attach their own features (the MCP head adds Smart Edit).
        /// </summary>
        public Action<MainWindow>? MainWindowCreated { get; init; }

        /// <summary>
        /// The source of the build the MCP server just sent (the desktop hosts send none). The window
        /// only runs builds; a head that works on code (Smart Edit) keeps it.
        /// </summary>
        public Action<string>? SourceCodeReceived { get; init; }
    }

    /// <summary>Entry point shared by all Avalonia plugin window executables.</summary>
    public static class PluginHost
    {
        public static PluginHostOptions Options { get; private set; } = new();

        public static int Run(string[] args, PluginHostOptions? options = null)
        {
            Options = options ?? new PluginHostOptions();
            // Windows-1252 and friends: tools read such files, and .NET knows them only after this.
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            WebViewDefaults.Install();
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}
