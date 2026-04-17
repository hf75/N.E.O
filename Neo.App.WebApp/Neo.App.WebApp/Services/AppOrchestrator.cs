using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Neo.App.WebApp.Services.Ai;
using Neo.App.WebApp.Services.Compilation;
using Neo.App.WebApp.Services.Sessions;
using Microsoft.CodeAnalysis;

namespace Neo.App.WebApp.Services;

public enum OrchestratorStatus { Idle, Streaming, Compiling, Loading, Done, Failed }

public sealed class OrchestratorEvent
{
    public OrchestratorStatus Status { get; init; }
    public string? Message { get; init; }
    public long? Elapsed { get; init; }
}

/// <summary>
/// Web analogue of Neo.App.Core.AppController: takes a user prompt, streams
/// the AI response, parses out structured fields, compiles any emitted C#
/// source, loads it into the in-process plugin host, and streams status
/// updates back to the UI.
/// </summary>
public sealed class AppOrchestrator
{
    private readonly AiClient _ai;
    private readonly WasmCompiler _compiler;
    private readonly InProcessPluginHost _pluginHost;

    public string CurrentProviderId { get; set; } = "claude";
    public string? CurrentModel { get; set; }
    public ObservableCollection<ChatEntry> History { get; } = new();
    public string? LastCode { get; private set; }
    public NuGetRef[]? LastNuGet { get; private set; }
    public System.Collections.Generic.IReadOnlyDictionary<string, byte[]>? LastDepAssemblies { get; private set; }

    public event Action<OrchestratorEvent>? StatusChanged;

    /// <summary>Fires every time a new chunk of the assistant's reply arrives.</summary>
    public event Action<string>? AssistantChunk;

    /// <summary>Fires when the assistant's reply is complete — parameter is the display text for the chat.</summary>
    public event Action<string>? AssistantComplete;

    private readonly NuGetResolver? _nuget;

    public AppOrchestrator(AiClient ai, WasmCompiler compiler, InProcessPluginHost pluginHost,
                           NuGetResolver? nuget = null)
    {
        _ai = ai;
        _compiler = compiler;
        _pluginHost = pluginHost;
        _nuget = nuget;
    }

    public InProcessPluginHost PluginHost => _pluginHost;
    public AiClient AiClient => _ai;
    public WasmCompiler Compiler => _compiler;
    public NuGetResolver? NuGet => _nuget;

    public async Task<Control?> ExecutePromptAsync(string userPrompt, CancellationToken ct = default)
    {
        // Snapshot the history BEFORE appending the new user turn, so the AI
        // sees the conversation up to and including its own last reply but
        // doesn't get the current prompt twice.
        var historyForAi = History
            .Select(h => new ChatTurn(h.Role, h.Content))
            .ToList();

        History.Add(new ChatEntry { Role = "user", Content = userPrompt, DisplayText = userPrompt });
        // Placeholder assistant entry that fills as tokens arrive — gives the
        // chat UI something to show live while the model is still streaming.
        var assistantEntry = new ChatEntry { Role = "assistant", Content = "", DisplayText = "…" };
        History.Add(assistantEntry);
        Emit(OrchestratorStatus.Streaming, "AI streaming…");

        var started = Environment.TickCount64;
        var sb = new StringBuilder();
        string? error = null;

        await foreach (var chunk in _ai.StreamAsync(
            CurrentProviderId, userPrompt,
            model: CurrentModel,
            systemPrompt: SystemPrompts.AvaloniaBrowser,
            history: historyForAi,
            ct: ct))
        {
            switch (chunk.Kind)
            {
                case "text":
                    sb.Append(chunk.Data);
                    // Keep the UI fed: show recent characters only, because the
                    // raw JSON would otherwise dump the full generated source
                    // into the chat bubble during streaming.
                    assistantEntry.DisplayText = "streaming… " + (sb.Length > 200
                        ? sb.ToString(sb.Length - 200, 200)
                        : sb.ToString());
                    AssistantChunk?.Invoke(chunk.Data);
                    break;
                case "error":
                    error = chunk.Data;
                    break;
            }
        }

        var raw = sb.ToString();
        // Keep the raw JSON in Content so the next turn feeds it back to the
        // model; overwrite DisplayText below with the human-readable summary.
        assistantEntry.Content = raw;

        if (error is not null)
        {
            assistantEntry.DisplayText = "⚠ " + error;
            Emit(OrchestratorStatus.Failed, "AI error: " + error);
            AssistantComplete?.Invoke("⚠ " + error);
            return null;
        }

        var parsed = StructuredResponseParser.Parse(raw);
        var chatText = parsed?.Chat
                       ?? parsed?.Explanation
                       ?? (parsed?.Code is null ? raw : "(code updated)");
        assistantEntry.DisplayText = chatText;
        AssistantComplete?.Invoke(chatText);

        if (parsed is null || string.IsNullOrWhiteSpace(parsed.Code))
        {
            Emit(OrchestratorStatus.Done, chatText);
            return null;
        }

        // Resolve any NuGet packages the AI requested.
        System.Collections.Generic.IReadOnlyList<Microsoft.CodeAnalysis.MetadataReference>? extraRefs = null;
        System.Collections.Generic.IReadOnlyDictionary<string, byte[]>? depAssemblies = null;
        if (parsed.NuGet is { Length: > 0 } && _nuget is not null)
        {
            var pkgList = string.Join(", ", parsed.NuGet.Select(p => p.Id));
            Emit(OrchestratorStatus.Compiling,
                $"Resolving NuGet: {pkgList} (first time may take 10-30s while the backend downloads from nuget.org)…");
            var nugetSw = System.Diagnostics.Stopwatch.StartNew();
            var resolved = await _nuget.ResolveAsync(parsed.NuGet);
            nugetSw.Stop();
            extraRefs = resolved.References;
            depAssemblies = resolved.AssemblyBytes;
            if (resolved.Errors.Count > 0)
                Emit(OrchestratorStatus.Compiling,
                    $"NuGet warnings ({nugetSw.ElapsedMilliseconds} ms): " + string.Join("; ", resolved.Errors));
            else
                Emit(OrchestratorStatus.Compiling,
                    $"NuGet resolved ({resolved.AssemblyBytes.Count} DLL{(resolved.AssemblyBytes.Count == 1 ? "" : "s")} in {nugetSw.ElapsedMilliseconds} ms).");
        }

        Emit(OrchestratorStatus.Compiling, "Compiling generated code…");
        var compile = await _compiler.CompileAsync(
            parsed.Code,
            $"Generated_{DateTime.UtcNow.Ticks}",
            extraRefs);
        if (!compile.Success)
        {
            Emit(OrchestratorStatus.Failed,
                "Compile failed:\n  " + string.Join("\n  ", compile.Diagnostics));
            return null;
        }

        Emit(OrchestratorStatus.Loading,
            $"Loading {compile.AssemblySize} B + {depAssemblies?.Count ?? 0} dep(s) into plugin host…");
        Control? control;
        try
        {
            control = _pluginHost.LoadFromBytes(compile.AssemblyBytes!, pdbBytes: null, depAssemblies);
            LastCode = parsed.Code;
            LastNuGet = parsed.NuGet;
            LastDepAssemblies = depAssemblies;
        }
        catch (Exception ex)
        {
            // Drill into inner exceptions so the UI shows the root cause
            // instead of the generic TargetInvocationException wrapper.
            var full = ex;
            while (full.InnerException is not null) full = full.InnerException;
            // Dump the whole chain to console for DevTools debugging.
            System.Diagnostics.Debug.WriteLine("[Neo.WebApp] Load failed:");
            System.Diagnostics.Debug.WriteLine(ex.ToString());
            Emit(OrchestratorStatus.Failed, "Load failed: " + full.GetType().Name + ": " + full.Message);
            return null;
        }

        var elapsed = Environment.TickCount64 - started;
        Emit(OrchestratorStatus.Done,
            $"Done in {elapsed} ms. Compile: {compile.CompileTimeMs} ms, DLL: {compile.AssemblySize} B.",
            elapsed);
        return control;
    }

    public NeoSession ToSession(string name)
    {
        var s = new NeoSession
        {
            Name = name,
            Code = LastCode ?? "",
            History = History.ToList(),
            UpdatedUtc = DateTime.UtcNow.ToString("O"),
        };
        if (LastNuGet is { Length: > 0 })
            s.NuGet = LastNuGet.Select(p => $"{p.Id}@{p.Version}").ToList();
        return s;
    }

    public async Task<bool> LoadSessionAsync(NeoSession session, CancellationToken ct = default)
    {
        History.Clear();
        foreach (var h in session.History)
        {
            // Restore human-readable display text for assistant turns that
            // only hold the raw JSON in Content.
            if (h.Role == "assistant" && string.IsNullOrEmpty(h.DisplayText) || h.DisplayText == h.Content)
            {
                var parsed = StructuredResponseParser.Parse(h.Content);
                h.DisplayText = parsed?.Chat
                                ?? parsed?.Explanation
                                ?? (parsed?.Code is null ? h.Content : "(code restored)");
            }
            History.Add(h);
        }
        LastCode = session.Code;
        if (string.IsNullOrWhiteSpace(session.Code)) return true;

        // Re-resolve the session's NuGet deps before restoring, so the plugin
        // has the same references as when it was first compiled.
        System.Collections.Generic.IReadOnlyList<Microsoft.CodeAnalysis.MetadataReference>? extraRefs = null;
        System.Collections.Generic.IReadOnlyDictionary<string, byte[]>? depAssemblies = null;
        if (session.NuGet is { Count: > 0 } && _nuget is not null)
        {
            var packages = session.NuGet
                .Select(s => s.Split('@', 2))
                .Where(p => p.Length >= 1 && !string.IsNullOrWhiteSpace(p[0]))
                .Select(p => new NuGetRef { Id = p[0].Trim(), Version = p.Length > 1 ? p[1].Trim() : "default" })
                .ToArray();
            Emit(OrchestratorStatus.Compiling, $"Restoring {packages.Length} NuGet package(s)…");
            var resolved = await _nuget.ResolveAsync(packages);
            extraRefs = resolved.References;
            depAssemblies = resolved.AssemblyBytes;
            LastNuGet = packages;
            LastDepAssemblies = depAssemblies;
        }

        Emit(OrchestratorStatus.Compiling, "Restoring compiled plugin…");
        var compile = await _compiler.CompileAsync(session.Code, $"Restored_{DateTime.UtcNow.Ticks}", extraRefs);
        if (!compile.Success)
        {
            Emit(OrchestratorStatus.Failed,
                "Restore compile failed:\n  " + string.Join("\n  ", compile.Diagnostics));
            return false;
        }
        try { _pluginHost.LoadFromBytes(compile.AssemblyBytes!, pdbBytes: null, depAssemblies); }
        catch (Exception ex)
        {
            var full = ex;
            while (full.InnerException is not null) full = full.InnerException;
            Emit(OrchestratorStatus.Failed, "Restore load failed: " + full.GetType().Name + ": " + full.Message);
            return false;
        }

        Emit(OrchestratorStatus.Done, "Session restored.");
        return true;
    }

    private void Emit(OrchestratorStatus status, string? message, long? elapsed = null)
        => StatusChanged?.Invoke(new OrchestratorEvent { Status = status, Message = message, Elapsed = elapsed });
}
