using System;
using System.Collections.Generic;
using Neo.Agents.Core;
using Neo.AssemblyForge;

namespace Neo.App
{
    /// <summary>
    /// System prompts of the desktop hosts (and of Smart Edit in the MCP plugin windows).
    ///
    /// <para>All prompt text lives in <see cref="AssemblyForgeSystemMessages"/>; this class only
    /// selects the desktop-host profile — the hosts compile PythonHost, PythonModuleLoader and the
    /// AIQuery helper into every generated app — and appends the documentation of the
    /// auto-discovered plugin agents.</para>
    /// </summary>
    public static class AISystemMessages
    {
        public static string GetSystemMessage(bool useAvalonia = false, bool useReact = false, bool usePython = false, IEnumerable<IAppIntegratedAgent>? pluginAgents = null)
        {
            if (useAvalonia == true && useReact == true)
                throw new NotImplementedException("Avalonia and React cannot be used in conjunction, yet!");

            string sp = AssemblyForgeSystemMessages.GetSystemMessage(
                useAvalonia ? AssemblyForgeUiFramework.Avalonia : AssemblyForgeUiFramework.Wpf,
                useReact,
                usePython,
                AssemblyForgePromptProfile.NeoDesktopHost);

            // Append system message docs from auto-discovered plugin agents
            if (pluginAgents != null)
            {
                foreach (var plugin in pluginAgents)
                {
                    if (!string.IsNullOrWhiteSpace(plugin.SystemMessageDocs))
                        sp += plugin.SystemMessageDocs;
                }
            }

            return sp;
        }

        public static string GetPatchReviewerSystemMessage()
            => AssemblyForgeSystemMessages.GetPatchReviewerSystemMessage();
    }
}
