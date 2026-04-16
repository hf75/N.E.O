using FluentAssertions;
using Xunit;

namespace Neo.AssemblyForge.Tests;

public class AssemblyForgeResultTests
{
    #region AssemblyForgeSessionState

    [Fact]
    public void SessionState_DefaultValues_AreEmpty()
    {
        var state = new AssemblyForgeSessionState();

        state.History.Should().BeEmpty();
        state.CurrentCode.Should().BeEmpty();
        state.NuGetDlls.Should().BeEmpty();
        state.PackageVersions.Should().BeEmpty();
        state.LastErrorMessage.Should().BeEmpty();
    }

    [Fact]
    public void SessionState_Clone_CreatesIndependentCopy()
    {
        var original = new AssemblyForgeSessionState
        {
            History = "history",
            CurrentCode = "code",
            NuGetDlls = new List<string> { "a.dll" },
            PackageVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Pkg1"] = "1.0"
            },
            LastErrorMessage = "error",
        };

        var clone = original.Clone();

        clone.History.Should().Be("history");
        clone.CurrentCode.Should().Be("code");
        clone.NuGetDlls.Should().ContainSingle("a.dll");
        clone.PackageVersions.Should().ContainKey("Pkg1");
        clone.LastErrorMessage.Should().Be("error");

        // Modify original and verify clone is unaffected
        original.History = "changed";
        original.CurrentCode = "changed";
        original.NuGetDlls.Add("b.dll");
        original.PackageVersions["Pkg2"] = "2.0";
        original.LastErrorMessage = "changed";

        clone.History.Should().Be("history");
        clone.CurrentCode.Should().Be("code");
        clone.NuGetDlls.Should().HaveCount(1);
        clone.PackageVersions.Should().HaveCount(1);
        clone.LastErrorMessage.Should().Be("error");
    }

    [Fact]
    public void SessionState_Clone_PreservesAllFields()
    {
        var original = new AssemblyForgeSessionState
        {
            History = "H",
            CurrentCode = "C",
            NuGetDlls = new List<string> { "x.dll", "y.dll" },
            PackageVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["A"] = "1", ["B"] = "2"
            },
            LastErrorMessage = "E",
        };

        var clone = original.Clone();

        clone.History.Should().Be(original.History);
        clone.CurrentCode.Should().Be(original.CurrentCode);
        clone.NuGetDlls.Should().BeEquivalentTo(original.NuGetDlls);
        clone.PackageVersions.Should().BeEquivalentTo(original.PackageVersions);
        clone.LastErrorMessage.Should().Be(original.LastErrorMessage);
    }

    [Fact]
    public void SessionState_Clone_PackageVersionsAreCaseInsensitive()
    {
        var original = new AssemblyForgeSessionState();
        original.PackageVersions["MyPackage"] = "1.0";

        var clone = original.Clone();

        clone.PackageVersions.Should().ContainKey("mypackage");
    }

    #endregion

    #region AssemblyForgeResult

    [Fact]
    public void Result_RequiredStatus_IsAccessible()
    {
        var result = new AssemblyForgeResult
        {
            Status = AssemblyForgeStatus.Success,
        };

        result.Status.Should().Be(AssemblyForgeStatus.Success);
    }

    [Fact]
    public void Result_DefaultValues_AreCorrect()
    {
        var result = new AssemblyForgeResult
        {
            Status = AssemblyForgeStatus.Failed,
        };

        result.ArtifactKind.Should().Be(AssemblyForgeArtifactKind.UserControlDll);
        result.OutputDllPath.Should().BeNull();
        result.OutputExePath.Should().BeNull();
        result.OutputExeDirectory.Should().BeNull();
        result.NuGetDllPaths.Should().BeEmpty();
        result.AdditionalDllPaths.Should().BeEmpty();
        result.StructuredResponse.Should().BeNull();
        result.ErrorMessage.Should().BeEmpty();
        result.AttemptsUsed.Should().Be(0);
    }

    [Fact]
    public void Result_WithAllFields_IsAccessible()
    {
        var response = new StructuredResponse { Code = "code" };
        var result = new AssemblyForgeResult
        {
            Status = AssemblyForgeStatus.Success,
            ArtifactKind = AssemblyForgeArtifactKind.Executable,
            OutputDllPath = "/dll.dll",
            OutputExePath = "/exe.exe",
            OutputExeDirectory = "/exedir",
            NuGetDllPaths = new[] { "a.dll" },
            AdditionalDllPaths = new[] { "b.dll" },
            StructuredResponse = response,
            ErrorMessage = "some error",
            AttemptsUsed = 3,
        };

        result.Status.Should().Be(AssemblyForgeStatus.Success);
        result.ArtifactKind.Should().Be(AssemblyForgeArtifactKind.Executable);
        result.OutputDllPath.Should().Be("/dll.dll");
        result.OutputExePath.Should().Be("/exe.exe");
        result.OutputExeDirectory.Should().Be("/exedir");
        result.NuGetDllPaths.Should().ContainSingle("a.dll");
        result.AdditionalDllPaths.Should().ContainSingle("b.dll");
        result.StructuredResponse.Should().BeSameAs(response);
        result.ErrorMessage.Should().Be("some error");
        result.AttemptsUsed.Should().Be(3);
    }

    #endregion
}
