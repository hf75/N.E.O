namespace Neo.App.Mcp.Internal;

/// <summary>One <c>[McpCallable]</c> method discovered on the loaded UserControl.</summary>
internal sealed record CallableEntry(
    string Name,
    string Description,
    IReadOnlyList<ParamEntry> Parameters,
    string ReturnTypeName,
    bool OffUiThread,
    int TimeoutSeconds);

internal sealed record ParamEntry(string Name, string TypeName);

/// <summary>One <c>[McpObservable]</c> property discovered on the loaded UserControl.</summary>
internal sealed record ObservableEntry(
    string Name,
    string Description,
    string TypeName,
    bool Watchable);

/// <summary>One <c>[McpTriggerable]</c> control reference. Reserved for input-pipeline integration.</summary>
internal sealed record TriggerableEntry(
    string Name,
    string Description,
    string ControlName,
    string ControlType);

/// <summary>The full capability surface of one Frozen-Mode app. Built once at startup.</summary>
internal sealed record AppManifest(
    string ClassFullName,
    IReadOnlyList<CallableEntry> Callables,
    IReadOnlyList<ObservableEntry> Observables,
    IReadOnlyList<TriggerableEntry> Triggerables);
