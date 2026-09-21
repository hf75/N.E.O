using Avalonia;
using System;

namespace Neo.PluginWindowAvalonia
{
    internal class Program
    {
        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        //
        // This head is the child process of the desktop hosts (Neo.App, Neo.App.Avalonia).
        // All window logic lives in Neo.PluginWindowAvalonia.Core.
        [STAThread]
        public static void Main(string[] args) => PluginHost.Run(args);

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp() => PluginHost.BuildAvaloniaApp();
    }
}
