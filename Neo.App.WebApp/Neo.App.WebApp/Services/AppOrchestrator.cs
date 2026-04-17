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
using Neo.AssemblyForge;
using System.Collections.Generic;

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

    /// <summary>
    /// Number of additional retries after a compile or load failure — each
    /// retry feeds the error back to the AI and asks for a fix. Zero disables
    /// retrying entirely. Matches the Neo desktop default of up to 5 total
    /// attempts.
    /// </summary>
    public int MaxCompileRetries { get; set; } = 4;

    public string? LastCode { get; private set; }
    public List<string>? LastNuGet { get; private set; }
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
        var started = Environment.TickCount64;

        // Snapshot history BEFORE appending the new user turn (AI mustn't see
        // the current prompt twice) and add the user turn to the live history.
        var historyForAi = History.Select(h => new ChatTurn(h.Role, h.Content)).ToList();
        History.Add(new ChatEntry { Role = "user", Content = userPrompt, DisplayText = userPrompt });

        // Each attempt appends (a) its own assistant-turn to History and (b)
        // the turn snapshot to historyForAi for the next call. Retries feed
        // the compile error as a new user-turn between assistant turns.
        string promptForAttempt = userPrompt;
        for (int attempt = 0; attempt <= MaxCompileRetries; attempt++)
        {
            var (parsed, rawReply, streamError, assistantEntry) =
                await StreamSingleTurnAsync(promptForAttempt, historyForAi, ct);

            if (streamError is not null)
            {
                assistantEntry.DisplayText = "⚠ " + streamError;
                Emit(OrchestratorStatus.Failed, "AI error: " + streamError);
                AssistantComplete?.Invoke("⚠ " + streamError);
                return null;
            }

            // Record what the AI said for the NEXT retry iteration.
            historyForAi.Add(new ChatTurn("user", promptForAttempt));
            historyForAi.Add(new ChatTurn("assistant", rawReply));

            var chatText = parsed?.Chat
                           ?? parsed?.Explanation
                           ?? (parsed?.Code is null && string.IsNullOrWhiteSpace(parsed?.Patch) ? rawReply : "(code updated)");
            assistantEntry.DisplayText = chatText;
            AssistantComplete?.Invoke(chatText);

            if (parsed is null)
            {
                Emit(OrchestratorStatus.Done, chatText);
                return null;
            }

            // Resolve patch → code if the AI sent a diff instead of full code.
            // Shared with desktop Neo via source-linked UnifiedDiffPatcher.
            string? codeToCompile = parsed.Code;
            if (!string.IsNullOrWhiteSpace(parsed.Patch))
            {
                var baseCode = LastCode ?? "";
                var patchResult = UnifiedDiffPatcher.TryApply(
                    originalText: baseCode,
                    patchText: parsed.Patch,
                    targetFilePath: "GeneratedApp.cs",
                    expectedClassNameForFallback: null);
                if (patchResult.Success && !string.IsNullOrWhiteSpace(patchResult.PatchedText))
                {
                    codeToCompile = patchResult.PatchedText;
                    Emit(OrchestratorStatus.Compiling,
                        $"Applied patch ({parsed.Patch!.Length} B) against previous code.");
                }
                else if (string.IsNullOrWhiteSpace(codeToCompile))
                {
                    // Patch broken and no full-code fallback — ask for a fresh one.
                    if (attempt >= MaxCompileRetries)
                    {
                        Emit(OrchestratorStatus.Failed,
                            $"Patch could not be applied: {patchResult.ErrorMessage}");
                        return null;
                    }
                    promptForAttempt =
                        $"Your previous PATCH could not be applied ({patchResult.ErrorMessage}). " +
                        "Return a valid unified diff targeting 'GeneratedApp.cs' (must include at least one '@@' hunk), " +
                        "OR — if that's not feasible — return the FULL updated C# source in the `code` field.";
                    History.Add(new ChatEntry
                    {
                        Role = "user",
                        Content = promptForAttempt,
                        DisplayText = $"↻ Patch didn't apply ({patchResult.ErrorMessage}). Asking for a fresh one."
                    });
                    Emit(OrchestratorStatus.Streaming,
                        $"Patch rejected — requesting fix (retry {attempt + 1}/{MaxCompileRetries})…");
                    continue;
                }
            }

            if (string.IsNullOrWhiteSpace(codeToCompile))
            {
                // Neither code nor applicable patch → AI is chatting.
                Emit(OrchestratorStatus.Done, chatText);
                return null;
            }

            // Resolve any NuGet packages the AI requested.
            System.Collections.Generic.IReadOnlyList<Microsoft.CodeAnalysis.MetadataReference>? extraRefs = null;
            System.Collections.Generic.IReadOnlyDictionary<string, byte[]>? depAssemblies = null;
            if (parsed.NuGetPackages is { Count: > 0 } && _nuget is not null)
            {
                var pkgList = string.Join(", ",
                    parsed.NuGetPackages.Select(s => s.Split('|', 2)[0]));
                Emit(OrchestratorStatus.Compiling,
                    $"Resolving NuGet: {pkgList} (first time may take 10-30s while the backend downloads from nuget.org)…");
                var nugetSw = System.Diagnostics.Stopwatch.StartNew();
                var resolved = await _nuget.ResolveAsync(parsed.NuGetPackages);
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

            Emit(OrchestratorStatus.Compiling,
                attempt == 0 ? "Compiling generated code…" : $"Compiling retry {attempt}/{MaxCompileRetries}…");
            var compile = await _compiler.CompileAsync(
                codeToCompile,
                $"Generated_{DateTime.UtcNow.Ticks}",
                extraRefs);

            if (!compile.Success)
            {
                if (attempt >= MaxCompileRetries)
                {
                    Emit(OrchestratorStatus.Failed,
                        $"Compile failed after {attempt + 1} attempts:\n  "
                        + string.Join("\n  ", compile.Diagnostics));
                    return null;
                }
                // Build a follow-up prompt containing the errors and a
                // request to fix them. Next iteration sends this as the user
                // turn with the prior assistant code in history.
                promptForAttempt = BuildCompileErrorFollowUp(compile.Diagnostics);
                History.Add(new ChatEntry { Role = "user", Content = promptForAttempt, DisplayText = $"↻ Compile failed ({compile.Diagnostics.Length} error{(compile.Diagnostics.Length == 1 ? "" : "s")}). Asking the AI to fix." });
                Emit(OrchestratorStatus.Streaming, $"Compile failed — requesting fix (retry {attempt + 1}/{MaxCompileRetries})…");
                continue;
            }

            // Compile OK — try to load.
            Emit(OrchestratorStatus.Loading,
                $"Loading {compile.AssemblySize} B + {depAssemblies?.Count ?? 0} dep(s) into plugin host…");
            Control? control;
            try
            {
                control = _pluginHost.LoadFromBytes(compile.AssemblyBytes!, pdbBytes: null, depAssemblies);
                LastCode = codeToCompile;
                LastNuGet = parsed.NuGetPackages?.ToList();
                LastDepAssemblies = depAssemblies;
            }
            catch (Exception ex)
            {
                var full = ex;
                while (full.InnerException is not null) full = full.InnerException;
                System.Diagnostics.Debug.WriteLine("[Neo.WebApp] Load failed:");
                System.Diagnostics.Debug.WriteLine(ex.ToString());

                if (attempt >= MaxCompileRetries)
                {
                    Emit(OrchestratorStatus.Failed,
                        "Load failed: " + full.GetType().Name + ": " + full.Message);
                    return null;
                }
                // Ask the AI to fix runtime-load problems too (often a
                // missing dependency or a bad base-class reference).
                promptForAttempt = BuildLoadErrorFollowUp(full);
                History.Add(new ChatEntry { Role = "user", Content = promptForAttempt, DisplayText = $"↻ Load failed ({full.GetType().Name}). Asking the AI to fix." });
                Emit(OrchestratorStatus.Streaming, $"Load failed — requesting fix (retry {attempt + 1}/{MaxCompileRetries})…");
                continue;
            }

            var elapsed = Environment.TickCount64 - started;
            var msg = attempt == 0
                ? $"Done in {elapsed} ms. Compile: {compile.CompileTimeMs} ms, DLL: {compile.AssemblySize} B."
                : $"Done in {elapsed} ms after {attempt} retr{(attempt == 1 ? "y" : "ies")}. Compile: {compile.CompileTimeMs} ms, DLL: {compile.AssemblySize} B.";
            Emit(OrchestratorStatus.Done, msg, elapsed);
            return control;
        }

        // Unreachable: either we returned from inside the loop or the retry
        // limit branch returned null after failing.
        return null;
    }

    private async Task<(StructuredResponse? parsed, string raw, string? error, ChatEntry assistantEntry)>
        StreamSingleTurnAsync(string userPrompt, System.Collections.Generic.IReadOnlyList<ChatTurn> history, CancellationToken ct)
    {
        var assistantEntry = new ChatEntry { Role = "assistant", Content = "", DisplayText = "…" };
        History.Add(assistantEntry);
        Emit(OrchestratorStatus.Streaming, "AI streaming…");

        var sb = new StringBuilder();
        string? error = null;

        await foreach (var chunk in _ai.StreamAsync(
            CurrentProviderId, userPrompt,
            model: CurrentModel,
            systemPrompt: SystemPrompts.AvaloniaBrowser,
            history: history,
            ct: ct))
        {
            switch (chunk.Kind)
            {
                case "text":
                    sb.Append(chunk.Data);
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
        assistantEntry.Content = raw;
        var parsed = error is null ? StructuredResponseParser.Parse(raw) : null;
        return (parsed, raw, error, assistantEntry);
    }

    // Retry-prompt builders live in Neo.AssemblyForge.Core.CodeIterationHelpers
    // and are shared with Neo desktop's AssemblyForgeSession.
    private static string BuildCompileErrorFollowUp(string[] diagnostics)
        => CodeIterationHelpers.BuildCompileErrorFollowUp(diagnostics);

    private static string BuildLoadErrorFollowUp(Exception ex)
        => CodeIterationHelpers.BuildLoadErrorFollowUp(ex);

    public NeoSession ToSession(string name)
    {
        var s = new NeoSession
        {
            Name = name,
            Code = LastCode ?? "",
            History = History.ToList(),
            UpdatedUtc = DateTime.UtcNow.ToString("O"),
        };
        if (LastNuGet is { Count: > 0 })
            s.NuGet = LastNuGet.ToList();
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
            // Session stores NuGet specs as-is (Forge wire format: "Id|Version"),
            // though older sessions may still use "Id@Version" — accept both.
            var specs = session.NuGet
                .Select(s => s.Contains('|') ? s : s.Replace('@', '|'))
                .ToList();
            Emit(OrchestratorStatus.Compiling, $"Restoring {specs.Count} NuGet package(s)…");
            var resolved = await _nuget.ResolveAsync(specs);
            extraRefs = resolved.References;
            depAssemblies = resolved.AssemblyBytes;
            LastNuGet = specs;
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
