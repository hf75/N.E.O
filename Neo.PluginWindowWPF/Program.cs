using System;

namespace Neo.PluginWindowWPF
{
    internal static class Program
    {
        // This head is the embedded child process of the WPF desktop host (Neo.App).
        // All window logic lives in Neo.PluginWindowWPF.Core.
        [STAThread]
        public static int Main() => PluginHost.Run(new PluginHostOptions
        {
            // Neo.App shows its own placeholder over the embedded child while code is generated.
            ShowWaitOverlay = false,
        });
    }
}
