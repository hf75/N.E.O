using Avalonia;
using System;

namespace Neo.PluginWindowAvalonia.MCP
{
    internal class Program
    {
        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        //
        // This head is the child process of Neo.McpServer. All window logic lives in
        // Neo.PluginWindowAvalonia.Core; on top of it this head adds Smart Edit (Ctrl+K) and
        // turns on the MCP host features (Live-MCP manifest, Ai.Trigger channel to Claude).
        [STAThread]
        public static void Main(string[] args) => PluginHost.Run(args, new PluginHostOptions
        {
            StandaloneWindowTitle = "N.E.O. — Live Preview (MCP)",
            EnableMcpHostFeatures = true,
            MainWindowCreated = window => new SmartEditController(window).Attach(),
        });

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp() => PluginHost.BuildAvaloniaApp();
    }
}
