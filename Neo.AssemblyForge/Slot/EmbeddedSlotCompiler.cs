using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Neo.AssemblyForge.Completion;
using Neo.AssemblyForge.Utils;

namespace Neo.AssemblyForge.Slot;

/// <summary>
/// Compiles a UI fragment using AssemblyForge directly in-process.
/// Works identically in the host child process and in standalone exports.
/// </summary>
public sealed class EmbeddedSlotCompiler : ISlotCompiler
{
    private readonly IAssemblyForgeCompletionProvider _completionProvider;
    private readonly IReadOnlyList<string> _referenceAssemblyDirs;
    private readonly string _workspaceBasePath;

    public EmbeddedSlotCompiler(
        IAssemblyForgeCompletionProvider completionProvider,
        IReadOnlyList<string> referenceAssemblyDirs,
        string workspaceBasePath)
    {
        _completionProvider = completionProvider ?? throw new ArgumentNullException(nameof(completionProvider));
        _referenceAssemblyDirs = referenceAssemblyDirs ?? throw new ArgumentNullException(nameof(referenceAssemblyDirs));
        _workspaceBasePath = workspaceBasePath ?? throw new ArgumentNullException(nameof(workspaceBasePath));
    }

    public async Task<SlotCompileResult> CompileAsync(
        string prompt,
        string uiFramework,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return new SlotCompileResult(false, null, null, "Prompt must not be empty.", null, null);

        var slotId = Guid.NewGuid().ToString("N")[..8];
        var slotDir = Path.Combine(_workspaceBasePath, slotId);

        try
        {
            Directory.CreateDirectory(slotDir);

            var framework = string.Equals(uiFramework, "Avalonia", StringComparison.OrdinalIgnoreCase)
                ? AssemblyForgeUiFramework.Avalonia
                : AssemblyForgeUiFramework.Wpf;

            var targetFramework = $"net{Environment.Version.Major}.0"
                + (framework == AssemblyForgeUiFramework.Wpf ? "-windows" : "");

            var workspace = new AssemblyForgeWorkspace
            {
                NuGetPackageDirectory = Path.Combine(slotDir, "NuGet"),
                OutputDllPath = Path.Combine(slotDir, "SlotFragment.dll"),
                ReferenceAssemblyDirectories = _referenceAssemblyDirs,
                TargetFramework = targetFramework,
                AssemblyName = "SlotFragment",
            };

            var pipeline = new AssemblyForgePipelineOptions
            {
                MaxAttempts = 3,
                Temperature = 0.1f,
                TopP = 0.9f,
            };

            var systemMsg = AssemblyForgeSystemMessages.GetSlotSystemMessage(framework);

            var client = new AssemblyForgeClient(_completionProvider, workspace, pipeline);
            var session = client.CreateSession(new AssemblyForgeSessionOptions
            {
                UiFramework = framework,
                UserControlClassName = "DynamicUserControl",
                SystemMessageOverride = systemMsg,
            });

            var result = await session.RunAsync(prompt, ct);

            if (result.Status == AssemblyForgeStatus.ChatOnly)
            {
                return new SlotCompileResult(false, null, null,
                    result.StructuredResponse?.Chat ?? "The AI responded with a question instead of code.",
                    result.StructuredResponse?.Explanation, null);
            }

            if (result.Status != AssemblyForgeStatus.Success || string.IsNullOrEmpty(result.OutputDllPath))
            {
                return new SlotCompileResult(false, null, null,
                    result.ErrorMessage.Length > 0 ? result.ErrorMessage : "Compilation failed.",
                    result.StructuredResponse?.Explanation, null);
            }

            var dllBytes = await File.ReadAllBytesAsync(result.OutputDllPath, ct);

            return new SlotCompileResult(
                Success: true,
                DllBytes: dllBytes,
                TypeName: "DynamicUserControl",
                ErrorMessage: null,
                Explanation: result.StructuredResponse?.Explanation,
                NuGetDllPaths: result.NuGetDllPaths);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SlotCompileResult(false, null, null,
                $"Slot compilation error: {ex.Message}", null, null);
        }
        finally
        {
            // Clean up slot workspace after compilation (DLL bytes are in memory now)
            try
            {
                if (Directory.Exists(slotDir))
                    Directory.Delete(slotDir, recursive: true);
            }
            catch { /* best effort cleanup */ }
        }
    }
}
