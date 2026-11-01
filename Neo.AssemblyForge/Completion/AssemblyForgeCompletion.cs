using System.Threading;
using System.Threading.Tasks;

namespace Neo.AssemblyForge.Completion;

public sealed record AssemblyForgeCompletionRequest
{
    public required string Prompt { get; init; }
    public required string History { get; init; }
    public required string SystemMessage { get; init; }
    public required string JsonSchema { get; init; }

    public float Temperature { get; init; } = 0.1f;
    public float TopP { get; init; } = 0.9f;

    /// <summary>1 for the first try; higher when the code of the tries before did not build.</summary>
    public int Attempt { get; init; } = 1;
}

public interface IAssemblyForgeCompletionProvider
{
    Task<string> CompleteAsync(AssemblyForgeCompletionRequest request, CancellationToken cancellationToken);
}
