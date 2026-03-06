using System;
using System.Threading;
using System.Threading.Tasks;
using Neo.Agents.Core;

namespace Neo.AssemblyForge.Completion;

public sealed class AiApiAgentCompletionProvider : IAssemblyForgeCompletionProvider
{
    private readonly IAgent _agent;
    private readonly string _outputKey;

    public AiApiAgentCompletionProvider(IAgent agent, string outputKey = "Result")
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _outputKey = string.IsNullOrWhiteSpace(outputKey) ? "Result" : outputKey;
    }

    public async Task<string> CompleteAsync(AssemblyForgeCompletionRequest request, CancellationToken cancellationToken)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        _agent.SetInput("SystemMessage", request.SystemMessage ?? string.Empty);
        _agent.SetInput("Prompt", request.Prompt ?? string.Empty);
        _agent.SetInput("History", request.History ?? string.Empty);
        _agent.SetInput("JsonSchema", request.JsonSchema ?? string.Empty);

        _agent.SetOption("Temperature", request.Temperature);
        _agent.SetOption("TopP", request.TopP);

        await _agent.ExecuteAsync(cancellationToken);

        var result = _agent.GetOutput<string>(_outputKey);
        if (string.IsNullOrWhiteSpace(result))
            throw new InvalidOperationException($"Completion provider returned empty output ('{_outputKey}').");

        return result;
    }
}
