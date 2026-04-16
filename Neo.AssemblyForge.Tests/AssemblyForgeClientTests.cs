using FluentAssertions;
using Neo.AssemblyForge.Completion;
using Neo.AssemblyForge.Services;
using Xunit;

namespace Neo.AssemblyForge.Tests;

public class AssemblyForgeClientTests
{
    private static AssemblyForgeWorkspace CreateTestWorkspace() => new()
    {
        NuGetPackageDirectory = Path.Combine(Path.GetTempPath(), "neo-test-nuget"),
        OutputDllPath = Path.Combine(Path.GetTempPath(), "neo-test-out.dll"),
        ReferenceAssemblyDirectories = new[] { Path.GetTempPath() },
    };

    private static DelegateCompletionProvider CreateNoOpProvider()
        => new((_, _) => Task.FromResult("{}"));

    private static StubCompilationService CreateStubCompiler() => new();
    private static StubNuGetPackageService CreateStubNuGet() => new();

    [Fact]
    public void CreateSession_ReturnsNonNullSession()
    {
        var client = new AssemblyForgeClient(
            CreateNoOpProvider(),
            CreateTestWorkspace(),
            nuGetPackageService: CreateStubNuGet(),
            compilationService: CreateStubCompiler());

        var session = client.CreateSession();

        session.Should().NotBeNull();
    }

    [Fact]
    public void CreateSession_DefaultOptions_UsesWpfBaseCode()
    {
        var client = new AssemblyForgeClient(
            CreateNoOpProvider(),
            CreateTestWorkspace(),
            nuGetPackageService: CreateStubNuGet(),
            compilationService: CreateStubCompiler());

        var session = client.CreateSession();

        session.State.CurrentCode.Should().Contain("System.Windows");
        session.State.CurrentCode.Should().Contain("DynamicUserControl");
    }

    [Fact]
    public void CreateSession_AvaloniaFramework_UsesAvaloniaBaseCode()
    {
        var client = new AssemblyForgeClient(
            CreateNoOpProvider(),
            CreateTestWorkspace(),
            nuGetPackageService: CreateStubNuGet(),
            compilationService: CreateStubCompiler());

        var session = client.CreateSession(new AssemblyForgeSessionOptions
        {
            UiFramework = AssemblyForgeUiFramework.Avalonia,
        });

        session.State.CurrentCode.Should().Contain("Avalonia");
    }

    [Fact]
    public void CreateSession_HasVirtualProjectWithMainFile()
    {
        var client = new AssemblyForgeClient(
            CreateNoOpProvider(),
            CreateTestWorkspace(),
            nuGetPackageService: CreateStubNuGet(),
            compilationService: CreateStubCompiler());

        var session = client.CreateSession();

        session.Project.FileExists("./currentcode.cs").Should().BeTrue();
        session.Project.GetFileContent("./currentcode.cs").Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void CreateSession_AdditionalSourceFiles_AreAddedToProject()
    {
        var client = new AssemblyForgeClient(
            CreateNoOpProvider(),
            CreateTestWorkspace(),
            nuGetPackageService: CreateStubNuGet(),
            compilationService: CreateStubCompiler());

        var session = client.CreateSession(new AssemblyForgeSessionOptions
        {
            AdditionalSourceFiles = new Dictionary<string, string>
            {
                ["./helpers.cs"] = "class Helper {}",
                ["./models.cs"] = "class Model {}",
            }
        });

        session.Project.FileExists("./helpers.cs").Should().BeTrue();
        session.Project.FileExists("./models.cs").Should().BeTrue();
        session.Project.GetFileContent("./helpers.cs").Should().Be("class Helper {}");
    }

    [Fact]
    public void CreateSession_CustomInitialCode_OverridesDefault()
    {
        var customCode = "// my custom code";
        var client = new AssemblyForgeClient(
            CreateNoOpProvider(),
            CreateTestWorkspace(),
            nuGetPackageService: CreateStubNuGet(),
            compilationService: CreateStubCompiler());

        var session = client.CreateSession(new AssemblyForgeSessionOptions
        {
            InitialCode = customCode,
        });

        session.State.CurrentCode.Should().Be(customCode);
        session.Project.GetFileContent("./currentcode.cs").Should().Be(customCode);
    }

    [Fact]
    public void CreateSession_ExecutableArtifact_UsesExecutableBaseCode()
    {
        var client = new AssemblyForgeClient(
            CreateNoOpProvider(),
            CreateTestWorkspace(),
            nuGetPackageService: CreateStubNuGet(),
            compilationService: CreateStubCompiler());

        var session = client.CreateSession(new AssemblyForgeSessionOptions
        {
            ArtifactKind = AssemblyForgeArtifactKind.Executable,
        });

        session.State.CurrentCode.Should().Contain("static void Main");
    }

    [Fact]
    public void CreateSession_HistoryContainsPrefix()
    {
        var client = new AssemblyForgeClient(
            CreateNoOpProvider(),
            CreateTestWorkspace(),
            nuGetPackageService: CreateStubNuGet(),
            compilationService: CreateStubCompiler());

        var session = client.CreateSession();

        session.State.History.Should().StartWith("Code:\n\n");
    }

    #region Test doubles

    private sealed class StubCompilationService : ICompilationService
    {
        public Task<string> CompileToDllAsync(
            IReadOnlyList<string> sourceFiles,
            string dllOutputPath,
            IReadOnlyList<string> nugetDllPaths,
            IReadOnlyList<string> additionalDllPaths,
            string assemblyName,
            CancellationToken cancellationToken)
            => Task.FromResult(dllOutputPath);

        public Task<string> CompileToExeAsync(
            IReadOnlyList<string> sourceFiles,
            string outputDirectory,
            IReadOnlyList<string> nugetDllPaths,
            IReadOnlyList<string> additionalDllPaths,
            string assemblyName,
            string mainTypeName,
            string compileType,
            CancellationToken cancellationToken)
            => Task.FromResult(Path.Combine(outputDirectory, assemblyName + ".exe"));
    }

    private sealed class StubNuGetPackageService : INuGetPackageService
    {
        public Task<NuGetResult> LoadPackagesAsync(
            IReadOnlyDictionary<string, string> packages,
            IEnumerable<string> existingDlls,
            CancellationToken cancellationToken)
            => Task.FromResult(new NuGetResult(
                Array.Empty<string>(),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
    }

    #endregion
}
