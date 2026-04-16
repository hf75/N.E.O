using FluentAssertions;
using Xunit;

namespace Neo.AssemblyForge.Tests;

public class VirtualProjectTests
{
    [Fact]
    public void AddFile_NewFile_ReturnsTrue()
    {
        var project = new VirtualProject();

        var result = project.AddFile("main.cs", "class A {}");

        result.Should().BeTrue();
        project.FileExists("main.cs").Should().BeTrue();
    }

    [Fact]
    public void AddFile_DuplicatePath_ReturnsFalse()
    {
        var project = new VirtualProject();
        project.AddFile("main.cs", "class A {}");

        var result = project.AddFile("main.cs", "class B {}");

        result.Should().BeFalse();
        project.GetFileContent("main.cs").Should().Be("class A {}");
    }

    [Fact]
    public void AddFile_NullContent_StoresEmptyString()
    {
        var project = new VirtualProject();

        project.AddFile("empty.cs", null!);

        project.GetFileContent("empty.cs").Should().Be(string.Empty);
    }

    [Fact]
    public void DeleteFile_ExistingFile_ReturnsTrueAndRemoves()
    {
        var project = new VirtualProject();
        project.AddFile("main.cs", "code");

        var result = project.DeleteFile("main.cs");

        result.Should().BeTrue();
        project.FileExists("main.cs").Should().BeFalse();
    }

    [Fact]
    public void DeleteFile_MissingFile_ReturnsFalse()
    {
        var project = new VirtualProject();

        var result = project.DeleteFile("missing.cs");

        result.Should().BeFalse();
    }

    [Fact]
    public void UpdateFileContent_ExistingFile_ReturnsTrueAndUpdates()
    {
        var project = new VirtualProject();
        project.AddFile("main.cs", "old");

        var result = project.UpdateFileContent("main.cs", "new");

        result.Should().BeTrue();
        project.GetFileContent("main.cs").Should().Be("new");
    }

    [Fact]
    public void UpdateFileContent_MissingFile_ReturnsFalse()
    {
        var project = new VirtualProject();

        var result = project.UpdateFileContent("missing.cs", "content");

        result.Should().BeFalse();
    }

    [Fact]
    public void UpdateFileContent_NullContent_StoresEmptyString()
    {
        var project = new VirtualProject();
        project.AddFile("main.cs", "old");

        project.UpdateFileContent("main.cs", null!);

        project.GetFileContent("main.cs").Should().Be(string.Empty);
    }

    [Fact]
    public void RenameFile_ValidRename_ReturnsTrueAndRenames()
    {
        var project = new VirtualProject();
        project.AddFile("old.cs", "code");

        var result = project.RenameFile("old.cs", "new.cs");

        result.Should().BeTrue();
        project.FileExists("old.cs").Should().BeFalse();
        project.FileExists("new.cs").Should().BeTrue();
        project.GetFileContent("new.cs").Should().Be("code");
    }

    [Fact]
    public void RenameFile_TargetExists_ReturnsFalse()
    {
        var project = new VirtualProject();
        project.AddFile("a.cs", "A");
        project.AddFile("b.cs", "B");

        var result = project.RenameFile("a.cs", "b.cs");

        result.Should().BeFalse();
        project.GetFileContent("a.cs").Should().Be("A");
        project.GetFileContent("b.cs").Should().Be("B");
    }

    [Fact]
    public void RenameFile_SourceMissing_ReturnsFalse()
    {
        var project = new VirtualProject();

        var result = project.RenameFile("missing.cs", "new.cs");

        result.Should().BeFalse();
    }

    [Fact]
    public void FileExists_ExistingFile_ReturnsTrue()
    {
        var project = new VirtualProject();
        project.AddFile("file.cs", "code");

        project.FileExists("file.cs").Should().BeTrue();
    }

    [Fact]
    public void FileExists_MissingFile_ReturnsFalse()
    {
        var project = new VirtualProject();

        project.FileExists("missing.cs").Should().BeFalse();
    }

    [Fact]
    public void GetFileContent_ExistingFile_ReturnsContent()
    {
        var project = new VirtualProject();
        project.AddFile("file.cs", "hello");

        project.GetFileContent("file.cs").Should().Be("hello");
    }

    [Fact]
    public void GetFileContent_MissingFile_ReturnsNull()
    {
        var project = new VirtualProject();

        project.GetFileContent("missing.cs").Should().BeNull();
    }

    [Fact]
    public void GetAllFiles_ReturnsAllVirtualFileRecords()
    {
        var project = new VirtualProject();
        project.AddFile("a.cs", "A");
        project.AddFile("b.txt", "B");

        var files = project.GetAllFiles().ToList();

        files.Should().HaveCount(2);
        files.Should().Contain(f => f.Path == "a.cs" && f.Content == "A");
        files.Should().Contain(f => f.Path == "b.txt" && f.Content == "B");
    }

    [Fact]
    public void GetAllFilePaths_ReturnsAllPaths()
    {
        var project = new VirtualProject();
        project.AddFile("x.cs", "X");
        project.AddFile("y.txt", "Y");

        var paths = project.GetAllFilePaths().ToList();

        paths.Should().HaveCount(2);
        paths.Should().Contain("x.cs");
        paths.Should().Contain("y.txt");
    }

    [Fact]
    public void GetSourceCodeAsStrings_ReturnsOnlyCsFiles()
    {
        var project = new VirtualProject();
        project.AddFile("main.cs", "class A {}");
        project.AddFile("readme.txt", "text");
        project.AddFile("helper.CS", "class B {}");

        var sources = project.GetSourceCodeAsStrings();

        sources.Should().HaveCount(2);
        sources.Should().Contain("class A {}");
        sources.Should().Contain("class B {}");
    }

    [Fact]
    public void GetSourceCodeAsStrings_WithTemporaryReplacements_OverridesContent()
    {
        var project = new VirtualProject();
        project.AddFile("main.cs", "original");

        var replacements = new Dictionary<string, string> { { "main.cs", "replaced" } };
        var sources = project.GetSourceCodeAsStrings(temporaryReplacements: replacements);

        sources.Should().HaveCount(1);
        sources.Should().Contain("replaced");
    }

    [Fact]
    public void GetSourceCodeAsStrings_WithSkipList_ExcludesFiles()
    {
        var project = new VirtualProject();
        project.AddFile("main.cs", "keep");
        project.AddFile("skip.cs", "skip");

        var sources = project.GetSourceCodeAsStrings(skipList: new[] { "skip.cs" });

        sources.Should().HaveCount(1);
        sources.Should().Contain("keep");
    }

    [Fact]
    public void CaseInsensitivePaths_AddAndRetrieve()
    {
        var project = new VirtualProject();
        project.AddFile("Main.CS", "code");

        project.FileExists("main.cs").Should().BeTrue();
        project.FileExists("MAIN.CS").Should().BeTrue();
        project.GetFileContent("main.cs").Should().Be("code");
    }

    [Fact]
    public void CaseInsensitivePaths_AddDuplicate_ReturnsFalse()
    {
        var project = new VirtualProject();
        project.AddFile("Main.cs", "first");

        var result = project.AddFile("main.cs", "second");

        result.Should().BeFalse();
    }

    [Fact]
    public void GetSourceCodeAsStrings_WithSkipListCaseInsensitive_ExcludesFiles()
    {
        var project = new VirtualProject();
        project.AddFile("Main.cs", "keep");
        project.AddFile("Helper.cs", "skip");

        var sources = project.GetSourceCodeAsStrings(skipList: new[] { "helper.cs" });

        sources.Should().HaveCount(1);
        sources.Should().Contain("keep");
    }

    [Fact]
    public void GetSourceCodeAsStrings_TemporaryReplacementsAreCaseInsensitive()
    {
        var project = new VirtualProject();
        project.AddFile("Main.cs", "original");

        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "main.cs", "replaced" }
        };
        var sources = project.GetSourceCodeAsStrings(temporaryReplacements: replacements);

        sources.Should().HaveCount(1);
        sources.Should().Contain("replaced");
    }
}
