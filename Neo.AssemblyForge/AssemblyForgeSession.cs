using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Neo.AssemblyForge.Completion;
using Neo.AssemblyForge.Services;
using Neo.AssemblyForge.Utils;

namespace Neo.AssemblyForge;

public sealed class AssemblyForgeSession
{
    private readonly IAssemblyForgeCompletionProvider _completionProvider;
    private readonly INuGetPackageService _nuget;
    private readonly ICompilationService _compiler;
    private readonly AssemblyForgeWorkspace _workspace;
    private readonly AssemblyForgePipelineOptions _pipelineOptions;

    internal AssemblyForgeSession(
        IAssemblyForgeCompletionProvider completionProvider,
        INuGetPackageService nuget,
        ICompilationService compiler,
        AssemblyForgeWorkspace workspace,
        AssemblyForgePipelineOptions pipelineOptions,
        AssemblyForgeSessionOptions sessionOptions,
        AssemblyForgeSessionState state,
        VirtualProject project)
    {
        _completionProvider = completionProvider ?? throw new ArgumentNullException(nameof(completionProvider));
        _nuget = nuget ?? throw new ArgumentNullException(nameof(nuget));
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _pipelineOptions = pipelineOptions ?? new AssemblyForgePipelineOptions();

        Options = sessionOptions ?? throw new ArgumentNullException(nameof(sessionOptions));
        State = state ?? throw new ArgumentNullException(nameof(state));
        Project = project ?? throw new ArgumentNullException(nameof(project));
    }

    public AssemblyForgeSessionOptions Options { get; }
    public AssemblyForgeSessionState State { get; }
    public VirtualProject Project { get; }

    public async Task<AssemblyForgeResult> RunAsync(
        string prompt,
        CancellationToken cancellationToken = default,
        AssemblyForgeReviewCallback? reviewCallback = null)
    {
        prompt ??= string.Empty;

        var baseCode = Project.GetFileContent(Options.MainFilePath) ?? State.CurrentCode ?? string.Empty;
        var reviewBaseCode = baseCode;

        var workingNugetDlls = State.NuGetDlls.ToList();
        var workingPackageVersions = new Dictionary<string, string>(State.PackageVersions, StringComparer.OrdinalIgnoreCase);

        string originalPrompt = prompt;
        string historyBefore = State.History ?? string.Empty;

        string baseCodeForPatch = baseCode;
        string currentPrompt = BuildDiffFirstPrompt(originalPrompt, baseCodeForPatch, Options.MainFilePath);

        string historyForModel = historyBefore;
        string lastErrorForModel = State.LastErrorMessage ?? string.Empty;

        var allPackagesThisRun = new List<string>();
        var allPackagesThisRunSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int attempt = 1; attempt <= Math.Max(1, _pipelineOptions.MaxAttempts); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            StructuredResponse? structuredResponse = null;

            try
            {
                if (_pipelineOptions.ClearNuGetCacheOnCs0433 &&
                    lastErrorForModel.Contains("CS0433", StringComparison.Ordinal))
                {
                    FileSystemHelper.ClearDirectory(_workspace.NuGetPackageDirectory);
                    if (_pipelineOptions.ClearNuGetStateOnCacheClear)
                    {
                        workingNugetDlls.Clear();
                        workingPackageVersions.Clear();
                    }
                }

                string promptForAttempt = currentPrompt;
                if (!string.IsNullOrWhiteSpace(lastErrorForModel))
                    promptForAttempt += "\n\n" + lastErrorForModel;

                string systemMessage = Options.SystemMessageOverride
                    ?? (Options.ArtifactKind == AssemblyForgeArtifactKind.Executable
                        ? AssemblyForgeSystemMessages.GetExecutableSystemMessage(
                            uiFramework: Options.UiFramework,
                            useReact: Options.UseReactUi,
                            usePython: Options.UsePython,
                            mainTypeName: Options.ExecutableMainTypeName)
                        : AssemblyForgeSystemMessages.GetSystemMessage(
                            uiFramework: Options.UiFramework,
                            useReact: Options.UseReactUi,
                            usePython: Options.UsePython));

                var completionJson = await _completionProvider.CompleteAsync(
                    new AssemblyForgeCompletionRequest
                    {
                        Prompt = promptForAttempt,
                        History = historyForModel,
                        SystemMessage = systemMessage,
                        JsonSchema = AssemblyForgeJsonSchemata.StructuredResponse,
                        Temperature = _pipelineOptions.Temperature,
                        TopP = _pipelineOptions.TopP,
                    },
                    cancellationToken);

                structuredResponse = JsonConvert.DeserializeObject<StructuredResponse>(completionJson)
                    ?? new StructuredResponse();

                if (!string.IsNullOrWhiteSpace(structuredResponse.Chat))
                {
                    State.History = historyBefore + "\n\n" + originalPrompt + "\n\n" + structuredResponse.Chat;
                    State.LastErrorMessage = string.Empty;

                    return new AssemblyForgeResult
                    {
                        Status = AssemblyForgeStatus.ChatOnly,
                        ArtifactKind = Options.ArtifactKind,
                        StructuredResponse = structuredResponse,
                        AttemptsUsed = attempt,
                        AdditionalDllPaths = _workspace.AdditionalReferenceDllPaths,
                        NuGetDllPaths = workingNugetDlls,
                    };
                }

                if (!string.IsNullOrWhiteSpace(structuredResponse.PowerShellScript))
                {
                    State.LastErrorMessage = string.Empty;

                    return new AssemblyForgeResult
                    {
                        Status = AssemblyForgeStatus.PowerShellReady,
                        ArtifactKind = Options.ArtifactKind,
                        StructuredResponse = structuredResponse,
                        AttemptsUsed = attempt,
                        AdditionalDllPaths = _workspace.AdditionalReferenceDllPaths,
                        NuGetDllPaths = workingNugetDlls,
                    };
                }

                if (!string.IsNullOrWhiteSpace(structuredResponse.ConsoleAppCode))
                {
                    State.LastErrorMessage = string.Empty;

                    return new AssemblyForgeResult
                    {
                        Status = AssemblyForgeStatus.ConsoleAppReady,
                        ArtifactKind = Options.ArtifactKind,
                        StructuredResponse = structuredResponse,
                        AttemptsUsed = attempt,
                        AdditionalDllPaths = _workspace.AdditionalReferenceDllPaths,
                        NuGetDllPaths = workingNugetDlls,
                    };
                }

                if (structuredResponse.NuGetPackages != null)
                {
                    foreach (var p in structuredResponse.NuGetPackages)
                    {
                        if (string.IsNullOrWhiteSpace(p)) continue;
                        var trimmed = p.Trim();
                        if (allPackagesThisRunSet.Add(trimmed))
                            allPackagesThisRun.Add(trimmed);
                    }
                }

                string codeToTest = structuredResponse.Code ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(structuredResponse.Patch))
                {
                    var patchResult = UnifiedDiffPatcher.TryApply(
                        originalText: baseCodeForPatch,
                        patchText: structuredResponse.Patch,
                        targetFilePath: Options.MainFilePath,
                        expectedClassNameForFallback: Options.ArtifactKind == AssemblyForgeArtifactKind.Executable
                            ? Options.ExecutableMainTypeName.Split('.').Last()
                            : Options.UserControlClassName);

                    if (!patchResult.Success || string.IsNullOrWhiteSpace(patchResult.PatchedText))
                        throw new InvalidOperationException($"The generated patch could not be applied: {patchResult.ErrorMessage}");

                    codeToTest = patchResult.PatchedText;
                    structuredResponse.Code = codeToTest;
                }

                if (Options.ArtifactKind == AssemblyForgeArtifactKind.UserControlDll)
                {
                    if (!CodeValidators.ContainsNamedUserControl(codeToTest, Options.UserControlClassName))
                        throw new InvalidOperationException($"The generated code does not contain a valid '{Options.UserControlClassName}' UserControl.");
                }
                else
                {
                    if (!CodeValidators.ContainsEntrypoint(codeToTest, Options.ExecutableMainTypeName))
                        throw new InvalidOperationException($"The generated code does not contain a valid entrypoint '{Options.ExecutableMainTypeName}'.");
                }

                var candidateNugetStateBefore = workingNugetDlls.ToList();
                var candidatePackageVersionsBefore = new Dictionary<string, string>(workingPackageVersions, StringComparer.OrdinalIgnoreCase);

                var packageDict = ConvertPackageListToDictionary(structuredResponse.NuGetPackages);
                if (packageDict.Count > 0)
                {
                    var nugetResult = await _nuget.LoadPackagesAsync(packageDict, workingNugetDlls, cancellationToken);

                    workingNugetDlls = nugetResult.DllPaths.ToList();
                    foreach (var kv in nugetResult.PackageVersions)
                        workingPackageVersions[kv.Key] = kv.Value;
                }

                var changesToTest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [Options.MainFilePath] = codeToTest,
                };

                var codesToCompile = Project.GetSourceCodeAsStrings(temporaryReplacements: changesToTest);

                string? compiledDllPath = null;
                string? compiledExePath = null;
                string? compiledExeDirectory = null;

                if (Options.ArtifactKind == AssemblyForgeArtifactKind.Executable)
                {
                    compiledExeDirectory = _workspace.OutputExeDirectory;
                    if (string.IsNullOrWhiteSpace(compiledExeDirectory))
                    {
                        var dllDir = Path.GetDirectoryName(Path.GetFullPath(_workspace.OutputDllPath));
                        compiledExeDirectory = !string.IsNullOrWhiteSpace(dllDir)
                            ? Path.Combine(dllDir, "ExeOutput")
                            : Path.Combine(Path.GetTempPath(), "Neo.AssemblyForge", "ExeOutput");
                    }

                    compiledExePath = await _compiler.CompileToExeAsync(
                        sourceFiles: codesToCompile,
                        outputDirectory: compiledExeDirectory,
                        nugetDllPaths: workingNugetDlls,
                        additionalDllPaths: _workspace.AdditionalReferenceDllPaths,
                        assemblyName: _workspace.AssemblyName,
                        mainTypeName: Options.ExecutableMainTypeName,
                        compileType: Options.ExecutableCompileType,
                        cancellationToken: cancellationToken);
                }
                else
                {
                    compiledDllPath = await _compiler.CompileToDllAsync(
                        sourceFiles: codesToCompile,
                        dllOutputPath: _workspace.OutputDllPath,
                        nugetDllPaths: workingNugetDlls,
                        additionalDllPaths: _workspace.AdditionalReferenceDllPaths,
                        assemblyName: _workspace.AssemblyName,
                        cancellationToken: cancellationToken);
                }

                structuredResponse.Patch = UnifiedDiffGenerator.CreatePatch(
                    filePath: Options.MainFilePath,
                    oldText: reviewBaseCode,
                    newText: codeToTest);

                structuredResponse.NuGetPackages = allPackagesThisRun.ToList();

                if (reviewCallback != null)
                {
                    var decision = await reviewCallback(
                        new AssemblyForgeReviewContext(
                            UserPrompt: originalPrompt,
                            Patch: structuredResponse.Patch ?? string.Empty,
                            ResultingCode: codeToTest,
                            NuGetPackages: allPackagesThisRun,
                            Explanation: structuredResponse.Explanation ?? string.Empty,
                            Attempt: attempt),
                        cancellationToken);

                    if (decision.Action == AssemblyForgeReviewAction.Cancel)
                        throw new OperationCanceledException();

                    if (decision.Action == AssemblyForgeReviewAction.Reject)
                    {
                        workingNugetDlls = candidateNugetStateBefore;
                        workingPackageVersions = candidatePackageVersionsBefore;

                        State.LastErrorMessage = string.Empty;

                        return new AssemblyForgeResult
                        {
                            Status = AssemblyForgeStatus.Rejected,
                            ArtifactKind = Options.ArtifactKind,
                            StructuredResponse = structuredResponse,
                            AttemptsUsed = attempt,
                            AdditionalDllPaths = _workspace.AdditionalReferenceDllPaths,
                            NuGetDllPaths = workingNugetDlls,
                        };
                    }

                    if (decision.Action == AssemblyForgeReviewAction.Regenerate)
                    {
                        workingNugetDlls = candidateNugetStateBefore;
                        workingPackageVersions = candidatePackageVersionsBefore;

                        var regenInstruction = decision.RegenerationInstruction;
                        if (string.IsNullOrWhiteSpace(regenInstruction))
                            regenInstruction = "Please regenerate with a different approach.";

                        baseCodeForPatch = reviewBaseCode;
                        currentPrompt = BuildDiffFirstPrompt(
                            userPrompt: originalPrompt + "\n\n" + regenInstruction,
                            currentCode: baseCodeForPatch,
                            mainFilePath: Options.MainFilePath);

                        lastErrorForModel = string.Empty;
                        historyForModel = string.Empty;
                        continue;
                    }
                }

                var newHistory = historyBefore;
                newHistory += "\n\n" + originalPrompt;
                newHistory += AssemblyForgeHistoryFormatter.StructuredResponseToText(structuredResponse);

                State.History = newHistory;
                State.CurrentCode = codeToTest;
                State.NuGetDlls = workingNugetDlls;
                State.PackageVersions = workingPackageVersions;
                State.LastErrorMessage = string.Empty;

                Project.UpdateFileContent(Options.MainFilePath, codeToTest);

                return new AssemblyForgeResult
                {
                    Status = AssemblyForgeStatus.Success,
                    ArtifactKind = Options.ArtifactKind,
                    OutputDllPath = compiledDllPath,
                    OutputExePath = compiledExePath,
                    OutputExeDirectory = compiledExeDirectory,
                    StructuredResponse = structuredResponse,
                    AttemptsUsed = attempt,
                    AdditionalDllPaths = _workspace.AdditionalReferenceDllPaths,
                    NuGetDllPaths = workingNugetDlls,
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastErrorForModel = BuildErrorForModel(ex);
                historyForModel = string.Empty;

                if (!string.IsNullOrWhiteSpace(structuredResponse?.Code))
                    baseCodeForPatch = structuredResponse.Code;

                bool patchRelatedError =
                    !string.IsNullOrWhiteSpace(structuredResponse?.Patch) &&
                    ex.Message.Contains("patch", StringComparison.OrdinalIgnoreCase);

                string retryInstruction = patchRelatedError
                    ? $"Your previous PATCH could not be applied ({ex.Message}). Return a valid unified diff patch targeting '{Options.MainFilePath}' (must include at least one '@@' hunk). If you cannot, return CODE RESPONSE."
                    : "Please fix the compilation/syntax errors in the current code. Keep changes minimal.";

                currentPrompt = BuildDiffFirstPrompt(
                    userPrompt: originalPrompt + "\n\n" + retryInstruction,
                    currentCode: baseCodeForPatch,
                    mainFilePath: Options.MainFilePath);
            }
        }

        State.LastErrorMessage = lastErrorForModel;

        return new AssemblyForgeResult
        {
            Status = AssemblyForgeStatus.Failed,
            ArtifactKind = Options.ArtifactKind,
            ErrorMessage = lastErrorForModel,
            AttemptsUsed = Math.Max(1, _pipelineOptions.MaxAttempts),
            AdditionalDllPaths = _workspace.AdditionalReferenceDllPaths,
            NuGetDllPaths = workingNugetDlls,
        };
    }

    private static string BuildDiffFirstPrompt(string userPrompt, string currentCode, string mainFilePath)
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

    private static Dictionary<string, string> ConvertPackageListToDictionary(IEnumerable<string>? packageList)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (packageList == null)
            return result;

        foreach (var package in packageList)
        {
            if (string.IsNullOrWhiteSpace(package))
                continue;

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

    private static string BuildErrorForModel(Exception ex)
    {
        if (ex is null) return string.Empty;

        var inner = ex.InnerException?.Message;
        if (!string.IsNullOrWhiteSpace(inner))
            return $"Exception Message:\n{ex.Message}\n\nInner Exception:\n{inner}";

        return $"Exception Message:\n{ex.Message}";
    }
}
