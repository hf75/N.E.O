using FluentAssertions;
using Xunit;

namespace Neo.AssemblyForge.Tests;

public class AssemblyForgeOptionsTests
{
    #region AssemblyForgePipelineOptions

    [Fact]
    public void PipelineOptions_DefaultMaxAttempts_IsFive()
    {
        var options = new AssemblyForgePipelineOptions();

        options.MaxAttempts.Should().Be(5);
    }

    [Fact]
    public void PipelineOptions_DefaultClearNuGetCacheOnCs0433_IsTrue()
    {
        var options = new AssemblyForgePipelineOptions();

        options.ClearNuGetCacheOnCs0433.Should().BeTrue();
    }

    [Fact]
    public void PipelineOptions_DefaultClearNuGetStateOnCacheClear_IsTrue()
    {
        var options = new AssemblyForgePipelineOptions();

        options.ClearNuGetStateOnCacheClear.Should().BeTrue();
    }

    [Fact]
    public void PipelineOptions_DefaultTemperature_Is01()
    {
        var options = new AssemblyForgePipelineOptions();

        options.Temperature.Should().BeApproximately(0.1f, 0.001f);
    }

    [Fact]
    public void PipelineOptions_DefaultTopP_Is09()
    {
        var options = new AssemblyForgePipelineOptions();

        options.TopP.Should().BeApproximately(0.9f, 0.001f);
    }

    #endregion

    #region AssemblyForgeSessionOptions

    [Fact]
    public void SessionOptions_DefaultArtifactKind_IsUserControlDll()
    {
        var options = new AssemblyForgeSessionOptions();

        options.ArtifactKind.Should().Be(AssemblyForgeArtifactKind.UserControlDll);
    }

    [Fact]
    public void SessionOptions_DefaultUiFramework_IsWpf()
    {
        var options = new AssemblyForgeSessionOptions();

        options.UiFramework.Should().Be(AssemblyForgeUiFramework.Wpf);
    }

    [Fact]
    public void SessionOptions_DefaultMainFilePath_IsCurrentCode()
    {
        var options = new AssemblyForgeSessionOptions();

        options.MainFilePath.Should().Be("./currentcode.cs");
    }

    [Fact]
    public void SessionOptions_DefaultUserControlClassName_IsDynamicUserControl()
    {
        var options = new AssemblyForgeSessionOptions();

        options.UserControlClassName.Should().Be("DynamicUserControl");
    }

    [Fact]
    public void SessionOptions_DefaultExecutableMainTypeName_IsNeoDynamicDynamicProgram()
    {
        var options = new AssemblyForgeSessionOptions();

        options.ExecutableMainTypeName.Should().Be("Neo.Dynamic.DynamicProgram");
    }

    [Fact]
    public void SessionOptions_DefaultUseReactUi_IsFalse()
    {
        var options = new AssemblyForgeSessionOptions();

        options.UseReactUi.Should().BeFalse();
    }

    [Fact]
    public void SessionOptions_DefaultUsePython_IsFalse()
    {
        var options = new AssemblyForgeSessionOptions();

        options.UsePython.Should().BeFalse();
    }

    [Fact]
    public void SessionOptions_DefaultInitialCode_IsNull()
    {
        var options = new AssemblyForgeSessionOptions();

        options.InitialCode.Should().BeNull();
    }

    [Fact]
    public void SessionOptions_DefaultInitialHistoryPrefix_IsCodePrefix()
    {
        var options = new AssemblyForgeSessionOptions();

        options.InitialHistoryPrefix.Should().Be("Code:\n\n");
    }

    [Fact]
    public void SessionOptions_DefaultAdditionalSourceFiles_IsEmpty()
    {
        var options = new AssemblyForgeSessionOptions();

        options.AdditionalSourceFiles.Should().BeEmpty();
    }

    [Fact]
    public void SessionOptions_DefaultSystemMessageOverride_IsNull()
    {
        var options = new AssemblyForgeSessionOptions();

        options.SystemMessageOverride.Should().BeNull();
    }

    [Fact]
    public void SessionOptions_DefaultExecutableCompileType_IsWindows()
    {
        var options = new AssemblyForgeSessionOptions();

        options.ExecutableCompileType.Should().Be("WINDOWS");
    }

    #endregion

    #region AssemblyForgeWorkspace

    [Fact]
    public void Workspace_DefaultAssemblyName_IsDynamicUserControl()
    {
        var workspace = new AssemblyForgeWorkspace
        {
            NuGetPackageDirectory = "/tmp/nuget",
            OutputDllPath = "/tmp/out.dll",
            ReferenceAssemblyDirectories = new[] { "/refs" },
        };

        workspace.AssemblyName.Should().Be("DynamicUserControl");
    }

    [Fact]
    public void Workspace_DefaultAdditionalReferenceDllPaths_IsEmpty()
    {
        var workspace = new AssemblyForgeWorkspace
        {
            NuGetPackageDirectory = "/tmp/nuget",
            OutputDllPath = "/tmp/out.dll",
            ReferenceAssemblyDirectories = new[] { "/refs" },
        };

        workspace.AdditionalReferenceDllPaths.Should().BeEmpty();
    }

    #endregion

    #region AssemblyForgeReviewContext

    [Fact]
    public void ReviewContext_RecordsAllProperties()
    {
        var packages = new List<string> { "Pkg1" };
        var context = new AssemblyForgeReviewContext(
            UserPrompt: "make a button",
            Patch: "patch text",
            ResultingCode: "code text",
            NuGetPackages: packages,
            Explanation: "explanation",
            Attempt: 2);

        context.UserPrompt.Should().Be("make a button");
        context.Patch.Should().Be("patch text");
        context.ResultingCode.Should().Be("code text");
        context.NuGetPackages.Should().ContainSingle("Pkg1");
        context.Explanation.Should().Be("explanation");
        context.Attempt.Should().Be(2);
    }

    #endregion

    #region AssemblyForgeReviewDecision

    [Fact]
    public void ReviewDecision_AcceptAction_DefaultRegenerationInstruction()
    {
        var decision = new AssemblyForgeReviewDecision(AssemblyForgeReviewAction.Accept);

        decision.Action.Should().Be(AssemblyForgeReviewAction.Accept);
        decision.RegenerationInstruction.Should().BeNull();
    }

    [Fact]
    public void ReviewDecision_RegenerateWithInstruction()
    {
        var decision = new AssemblyForgeReviewDecision(
            AssemblyForgeReviewAction.Regenerate,
            "Try a different approach");

        decision.Action.Should().Be(AssemblyForgeReviewAction.Regenerate);
        decision.RegenerationInstruction.Should().Be("Try a different approach");
    }

    #endregion
}
