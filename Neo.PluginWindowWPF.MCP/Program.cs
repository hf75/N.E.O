using System;

namespace Neo.PluginWindowWPF.MCP
{
    internal static class Program
    {
        // This head is the child process of Neo.McpServer. All window logic lives in
        // Neo.PluginWindowWPF.Core; on top of it this head adds Smart Edit (Ctrl+K).
        [STAThread]
        public static int Main()
        {
            SmartEditController? smartEdit = null;

            return PluginHost.Run(new PluginHostOptions
            {
                StandaloneWindowTitle = "N.E.O. — Live Preview (WPF MCP)",
                MainWindowCreated = window =>
                {
                    smartEdit = new SmartEditController(window);
                    smartEdit.Attach();
                },
                LastCompiledCodeProvider = () => smartEdit?.LastCompiledCode,
                SourceCodeReceived = code => smartEdit?.SeedCode(code),
            });
        }
    }
}
