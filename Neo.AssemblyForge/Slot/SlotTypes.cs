using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Neo.AssemblyForge.Slot;

/// <summary>
/// Compiles a UI fragment from a natural language prompt.
/// </summary>
public interface ISlotCompiler
{
    Task<SlotCompileResult> CompileAsync(
        string prompt,
        string uiFramework,
        CancellationToken ct = default);
}

/// <summary>
/// Result of a slot compilation.
/// </summary>
public sealed record SlotCompileResult(
    bool Success,
    byte[]? DllBytes,
    string? TypeName,
    string? ErrorMessage,
    string? Explanation,
    IReadOnlyList<string>? NuGetDllPaths
);

/// <summary>
/// Static service locator for DynamicSlot compilation.
/// Set by the host process (child plugin window) at startup.
/// </summary>
public static class DynamicSlotService
{
    /// <summary>
    /// The compiler used by all DynamicSlot instances in this process.
    /// </summary>
    public static ISlotCompiler? Compiler { get; set; }

    /// <summary>
    /// Maximum nesting depth for DynamicSlots. Default: 1 (no nesting).
    /// </summary>
    public static int MaxDepth { get; set; } = 1;
}
