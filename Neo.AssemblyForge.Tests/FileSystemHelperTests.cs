using FluentAssertions;
using Neo.AssemblyForge.Utils;
using Xunit;

namespace Neo.AssemblyForge.Tests;

public class FileSystemHelperTests : IDisposable
{
    private readonly string _testDir;

    public FileSystemHelperTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "Neo.AssemblyForge.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, true);
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    [Fact]
    public void ClearDirectory_RemovesFilesAndSubdirectories()
    {
        File.WriteAllText(Path.Combine(_testDir, "file1.txt"), "content1");
        File.WriteAllText(Path.Combine(_testDir, "file2.txt"), "content2");
        var subDir = Path.Combine(_testDir, "subdir");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "nested.txt"), "nested");

        FileSystemHelper.ClearDirectory(_testDir);

        Directory.GetFiles(_testDir).Should().BeEmpty();
        Directory.GetDirectories(_testDir).Should().BeEmpty();
    }

    [Fact]
    public void ClearDirectory_NonExistentPath_NoException()
    {
        var nonExistent = Path.Combine(_testDir, "does_not_exist");

        var act = () => FileSystemHelper.ClearDirectory(nonExistent);

        act.Should().NotThrow();
    }

    [Fact]
    public void ClearDirectory_NullPath_NoException()
    {
        var act = () => FileSystemHelper.ClearDirectory(null!);

        act.Should().NotThrow();
    }

    [Fact]
    public void ClearDirectory_EmptyPath_NoException()
    {
        var act = () => FileSystemHelper.ClearDirectory("");

        act.Should().NotThrow();
    }

    [Fact]
    public void ClearDirectory_WhitespacePath_NoException()
    {
        var act = () => FileSystemHelper.ClearDirectory("   ");

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureDirectory_CreatesDirectory()
    {
        var newDir = Path.Combine(_testDir, "newsubdir");

        var result = FileSystemHelper.EnsureDirectory(newDir);

        result.Should().Be(newDir);
        Directory.Exists(newDir).Should().BeTrue();
    }

    [Fact]
    public void EnsureDirectory_ExistingDirectory_NoException()
    {
        var result = FileSystemHelper.EnsureDirectory(_testDir);

        result.Should().Be(_testDir);
    }

    [Fact]
    public void EnsureDirectory_NullPath_ThrowsArgumentException()
    {
        var act = () => FileSystemHelper.EnsureDirectory(null!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EnsureDirectory_EmptyPath_ThrowsArgumentException()
    {
        var act = () => FileSystemHelper.EnsureDirectory("");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EnsureDirectory_WhitespacePath_ThrowsArgumentException()
    {
        var act = () => FileSystemHelper.EnsureDirectory("   ");

        act.Should().Throw<ArgumentException>();
    }
}
