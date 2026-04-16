using FluentAssertions;
using Neo.App;
using Xunit;

namespace Neo.App.Core.Tests;

public class FileHelperTests : IDisposable
{
    private readonly string _tempDir;

    public FileHelperTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "NeoFileHelperTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch { /* best effort */ }
    }

    // ── CopyDlls ────────────────────────────────────────────────────

    [Fact]
    public void CopyDlls_CopiesFilesToTargetDirectory()
    {
        var sourceDir = Path.Combine(_tempDir, "source");
        Directory.CreateDirectory(sourceDir);
        var dll1 = Path.Combine(sourceDir, "lib1.dll");
        var dll2 = Path.Combine(sourceDir, "lib2.dll");
        File.WriteAllText(dll1, "fake dll 1");
        File.WriteAllText(dll2, "fake dll 2");

        var targetDir = Path.Combine(_tempDir, "target");

        FileHelper.CopyDlls(new List<string> { dll1, dll2 }, targetDir);

        File.Exists(Path.Combine(targetDir, "lib1.dll")).Should().BeTrue();
        File.Exists(Path.Combine(targetDir, "lib2.dll")).Should().BeTrue();
    }

    [Fact]
    public void CopyDlls_CreatesTargetDirectoryIfMissing()
    {
        var targetDir = Path.Combine(_tempDir, "newdir");

        FileHelper.CopyDlls(new List<string>(), targetDir);

        Directory.Exists(targetDir).Should().BeTrue();
    }

    [Fact]
    public void CopyDlls_SkipsNonExistentFiles()
    {
        var targetDir = Path.Combine(_tempDir, "target2");
        var nonExistent = Path.Combine(_tempDir, "doesnotexist.dll");

        // Should not throw.
        FileHelper.CopyDlls(new List<string> { nonExistent }, targetDir);

        Directory.GetFiles(targetDir).Should().BeEmpty();
    }

    [Fact]
    public void CopyDlls_OverwritesExistingFile()
    {
        var sourceDir = Path.Combine(_tempDir, "src");
        Directory.CreateDirectory(sourceDir);
        var dll = Path.Combine(sourceDir, "lib.dll");
        File.WriteAllText(dll, "version2");

        var targetDir = Path.Combine(_tempDir, "tgt");
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "lib.dll"), "version1");

        FileHelper.CopyDlls(new List<string> { dll }, targetDir);

        File.ReadAllText(Path.Combine(targetDir, "lib.dll")).Should().Be("version2");
    }

    // ── ClearDirectory ──────────────────────────────────────────────

    [Fact]
    public void ClearDirectory_RemovesFilesAndSubdirectories()
    {
        var dir = Path.Combine(_tempDir, "clearme");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "file.txt"), "data");
        Directory.CreateDirectory(Path.Combine(dir, "subdir"));
        File.WriteAllText(Path.Combine(dir, "subdir", "nested.txt"), "nested");

        FileHelper.ClearDirectory(dir);

        Directory.GetFiles(dir).Should().BeEmpty();
        Directory.GetDirectories(dir).Should().BeEmpty();
    }

    [Fact]
    public void ClearDirectory_NonExistentDirectory_DoesNotThrow()
    {
        var act = () => FileHelper.ClearDirectory(Path.Combine(_tempDir, "nonexistent"));

        act.Should().NotThrow();
    }

    // ── LoadAllCsFilesOneLevel ──────────────────────────────────────

    [Fact]
    public void LoadAllCsFilesOneLevel_LoadsRootAndOneSubLevel()
    {
        var rootDir = Path.Combine(_tempDir, "csfiles");
        Directory.CreateDirectory(rootDir);
        File.WriteAllText(Path.Combine(rootDir, "Root.cs"), "root content");

        var subDir = Path.Combine(rootDir, "Sub");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "Sub.cs"), "sub content");

        // Deep nested should NOT be picked up.
        var deepDir = Path.Combine(subDir, "Deep");
        Directory.CreateDirectory(deepDir);
        File.WriteAllText(Path.Combine(deepDir, "Deep.cs"), "deep content");

        var result = FileHelper.LoadAllCsFilesOneLevel(rootDir);

        result.Should().HaveCount(2);
        result.Should().Contain("root content");
        result.Should().Contain("sub content");
    }

    [Fact]
    public void LoadAllCsFilesOneLevel_NonExistentDirectory_ThrowsDirectoryNotFoundException()
    {
        var act = () => FileHelper.LoadAllCsFilesOneLevel(Path.Combine(_tempDir, "nope"));

        act.Should().Throw<DirectoryNotFoundException>();
    }

    [Fact]
    public void LoadAllCsFilesOneLevel_EmptyDirectory_ReturnsEmptyList()
    {
        var emptyDir = Path.Combine(_tempDir, "emptycs");
        Directory.CreateDirectory(emptyDir);

        var result = FileHelper.LoadAllCsFilesOneLevel(emptyDir);

        result.Should().BeEmpty();
    }

    [Fact]
    public void LoadAllCsFilesOneLevel_IgnoresNonCsFiles()
    {
        var dir = Path.Combine(_tempDir, "mixedfiles");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "File.cs"), "cs content");
        File.WriteAllText(Path.Combine(dir, "File.txt"), "txt content");
        File.WriteAllText(Path.Combine(dir, "File.json"), "json content");

        var result = FileHelper.LoadAllCsFilesOneLevel(dir);

        result.Should().HaveCount(1);
        result.Should().Contain("cs content");
    }

    // ── FindAllDllFilesOneLevel ─────────────────────────────────────

    [Fact]
    public void FindAllDllFilesOneLevel_FindsDllsRecursively()
    {
        var rootDir = Path.Combine(_tempDir, "dlls");
        Directory.CreateDirectory(rootDir);
        File.WriteAllText(Path.Combine(rootDir, "a.dll"), "a");

        var sub = Path.Combine(rootDir, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "b.dll"), "b");

        var result = FileHelper.FindAllDllFilesOneLevel(rootDir);

        result.Should().HaveCount(2);
    }

    [Fact]
    public void FindAllDllFilesOneLevel_NonExistentDirectory_ThrowsDirectoryNotFoundException()
    {
        var act = () => FileHelper.FindAllDllFilesOneLevel(Path.Combine(_tempDir, "nope"));

        act.Should().Throw<DirectoryNotFoundException>();
    }

    [Fact]
    public void FindAllDllFilesOneLevel_EmptyDirectory_ReturnsEmptyList()
    {
        var dir = Path.Combine(_tempDir, "emptydll");
        Directory.CreateDirectory(dir);

        var result = FileHelper.FindAllDllFilesOneLevel(dir);

        result.Should().BeEmpty();
    }
}
