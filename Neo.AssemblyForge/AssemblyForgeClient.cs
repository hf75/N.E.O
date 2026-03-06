using System;
using Neo.AssemblyForge.Completion;
using Neo.AssemblyForge.Services;

namespace Neo.AssemblyForge;

public sealed class AssemblyForgeClient
{
    private readonly IAssemblyForgeCompletionProvider _completionProvider;
    private readonly INuGetPackageService _nuget;
    private readonly ICompilationService _compiler;

    public AssemblyForgeClient(
        IAssemblyForgeCompletionProvider completionProvider,
        AssemblyForgeWorkspace workspace,
        AssemblyForgePipelineOptions? pipelineOptions = null,
        INuGetPackageService? nuGetPackageService = null,
        ICompilationService? compilationService = null)
    {
        _completionProvider = completionProvider ?? throw new ArgumentNullException(nameof(completionProvider));
        Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        PipelineOptions = pipelineOptions ?? new AssemblyForgePipelineOptions();

        _nuget = nuGetPackageService ?? new NuGetPackageService(
            nuGetPackageDirectory: Workspace.NuGetPackageDirectory,
            targetFramework: Workspace.TargetFramework,
            referenceAssemblyDirectories: Workspace.ReferenceAssemblyDirectories);

        _compiler = compilationService ?? new CompilationService(
            referenceAssemblyDirectories: Workspace.ReferenceAssemblyDirectories);
    }

    public AssemblyForgeWorkspace Workspace { get; }
    public AssemblyForgePipelineOptions PipelineOptions { get; }

    public AssemblyForgeSession CreateSession(AssemblyForgeSessionOptions? options = null)
    {
        options ??= new AssemblyForgeSessionOptions();

        var initialCode = options.InitialCode ??
                          (options.ArtifactKind == AssemblyForgeArtifactKind.Executable
                              ? AssemblyForgeTemplates.GetExecutableBaseCode(options.UiFramework, options.ExecutableMainTypeName)
                              : AssemblyForgeTemplates.GetBaseCode(options.UiFramework));
        var state = new AssemblyForgeSessionState
        {
            CurrentCode = initialCode,
            History = (options.InitialHistoryPrefix ?? string.Empty) + initialCode,
        };

        var project = new VirtualProject();
        project.AddFile(options.MainFilePath, initialCode);

        if (options.AdditionalSourceFiles != null)
        {
            foreach (var file in options.AdditionalSourceFiles)
                project.AddFile(file.Key, file.Value);
        }

        return new AssemblyForgeSession(
            completionProvider: _completionProvider,
            nuget: _nuget,
            compiler: _compiler,
            workspace: Workspace,
            pipelineOptions: PipelineOptions,
            sessionOptions: options,
            state: state,
            project: project);
    }
}
