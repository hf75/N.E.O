using System;
using System.Collections.Generic;

namespace Neo.AssemblyForge;

/// <summary>
/// Shared helpers used by every code-iteration loop in Neo: the desktop/MCP
/// <c>AssemblyForgeSession</c>, and the Web App's <c>AppOrchestrator</c>.
/// Each surface runs its own retry loop with its own storage/compile
/// primitives, but the prompt-building logic is identical and benefits from
/// living in one place.
/// </summary>
public static class CodeIterationHelpers
{
    /// <summary>
    /// Build the prompt that frames the current file content and asks the AI
    /// to either return a unified-diff patch or full-file code. Used on the
    /// first attempt and on retries after a patch-apply failure.
    /// </summary>
    public static string BuildDiffFirstPrompt(
        string userPrompt,
        string currentCode,
        string mainFilePath)
    {
        userPrompt ??= string.Empty;
        currentCode ??= string.Empty;
        mainFilePath ??= "./currentcode.cs";

        return "You are editing the existing C# file '" + mainFilePath + "'.\n\n" +
               "PATCH REQUIREMENTS: The Patch field must include at least one hunk header line starting with '@@' (prefer numeric unified diff like '@@ -10,7 +10,8 @@').\n\n" +
               "CURRENT FILE CONTENT:\n" +
               "```csharp\n" +
               currentCode +
               "\n```\n\n" +
               "TASK:\n" +
               userPrompt +
               "\n\n" +
               "Prefer PATCH RESPONSE. If the patch would be extremely large or cannot be made to apply cleanly, use CODE RESPONSE instead.";
    }

    /// <summary>
    /// Packages an exception into the short form the model gets to see on
    /// retry. Includes the inner exception when present — loader exceptions
    /// and compile-failures always hide their useful text there.
    /// </summary>
    public static string BuildErrorForModel(Exception ex)
    {
        if (ex is null) return string.Empty;

        var inner = ex.InnerException?.Message;
        if (!string.IsNullOrWhiteSpace(inner))
            return $"Exception Message:\n{ex.Message}\n\nInner Exception:\n{inner}";

        return $"Exception Message:\n{ex.Message}";
    }

    /// <summary>
    /// Build a user-turn the WebApp orchestrator appends on a Roslyn compile
    /// failure. The model sees its own prior code (via history) plus this
    /// message and is asked to fix.
    /// </summary>
    public static string BuildCompileErrorFollowUp(string[] diagnostics)
    {
        var joined = string.Join("\n", Take(diagnostics, 10));
        return
            "The code you just produced failed to compile with the following Roslyn errors:\n\n" +
            joined +
            "\n\nFix the errors and return the FULL updated C# source in the `Code` field of the JSON, " +
            "preserving every element, event handler, and behavior that wasn't at fault. " +
            "Do not apologise, do not explain — just the corrected JSON object.";
    }

    /// <summary>
    /// Build a user-turn for runtime-load failures — typically missing
    /// NuGet deps or APIs the WASM sandbox refuses at JIT time.
    /// </summary>
    public static string BuildLoadErrorFollowUp(Exception ex)
    {
        return
            "The code compiled but failed to load at runtime:\n\n" +
            $"{ex.GetType().Name}: {ex.Message}\n\n" +
            "This usually means a referenced type can't be resolved in the WASM sandbox (missing NuGet, " +
            "forbidden API, or an Avalonia API that doesn't exist). Fix the cause and return the FULL " +
            "updated C# source in the `Code` field of the JSON. If the fix requires an extra NuGet package, " +
            "include it in the `NuGetPackages` field as an \"Id|Version\" entry.";
    }

    /// <summary>
    /// Parse the AI's Forge-wire-format NuGet list (array of <c>"Id|Version"</c>
    /// strings) into a dictionary the resolver accepts. Empty/whitespace
    /// entries are ignored; a bare id with no version defaults to
    /// <c>"default"</c> (= latest stable).
    /// </summary>
    public static Dictionary<string, string> ConvertPackageListToDictionary(IEnumerable<string>? packageList)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (packageList == null) return result;

        foreach (var package in packageList)
        {
            if (string.IsNullOrWhiteSpace(package)) continue;

            var parts = package.Split('|', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                var name = parts[0].Trim();
                var version = parts[1].Trim();
                if (name.Length > 0)
                    result[name] = version.Length > 0 ? version : "default";
            }
            else if (parts.Length == 1)
            {
                var name = parts[0].Trim();
                if (name.Length > 0)
                    result[name] = "default";
            }
        }
        return result;
    }

    // System.Linq.Enumerable.Take isn't strictly needed, but keeping this
    // helper avoids a System.Linq dependency in the file for consumers that
    // target trimmed environments.
    private static IEnumerable<T> Take<T>(IEnumerable<T> source, int count)
    {
        if (source is null) yield break;
        int n = 0;
        foreach (var item in source)
        {
            if (n++ >= count) yield break;
            yield return item;
        }
    }
}
