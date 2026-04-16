using FluentAssertions;
using Neo.App;
using Xunit;

namespace Neo.App.Core.Tests;

public class EnumTypesTests
{
    // ── BubbleType ──────────────────────────────────────────────────

    [Theory]
    [InlineData(BubbleType.Prompt, 0)]
    [InlineData(BubbleType.Answer, 1)]
    [InlineData(BubbleType.CompletionError, 2)]
    [InlineData(BubbleType.CompletionSuccess, 3)]
    [InlineData(BubbleType.Info, 4)]
    public void BubbleType_HasExpectedIntegerValue(BubbleType value, int expected)
    {
        ((int)value).Should().Be(expected);
    }

    [Fact]
    public void BubbleType_HasExactly5Values()
    {
        Enum.GetValues<BubbleType>().Should().HaveCount(5);
    }

    // ── CrashReason ─────────────────────────────────────────────────

    [Theory]
    [InlineData(CrashReason.UnhandledException, 0)]
    [InlineData(CrashReason.HeartbeatTimeout, 1)]
    [InlineData(CrashReason.PipeDisconnected, 2)]
    public void CrashReason_HasExpectedIntegerValue(CrashReason value, int expected)
    {
        ((int)value).Should().Be(expected);
    }

    [Fact]
    public void CrashReason_HasExactly3Values()
    {
        Enum.GetValues<CrashReason>().Should().HaveCount(3);
    }

    // ── CreationMode ────────────────────────────────────────────────

    [Theory]
    [InlineData(CreationMode.FailIfExists, 0)]
    [InlineData(CreationMode.Overwrite, 1)]
    [InlineData(CreationMode.Merge, 2)]
    public void CreationMode_HasExpectedIntegerValue(CreationMode value, int expected)
    {
        ((int)value).Should().Be(expected);
    }

    [Fact]
    public void CreationMode_HasExactly3Values()
    {
        Enum.GetValues<CreationMode>().Should().HaveCount(3);
    }

    // ── CrossPlatformExport ─────────────────────────────────────────

    [Theory]
    [InlineData(CrossPlatformExport.NONE, 0)]
    [InlineData(CrossPlatformExport.WINDOWS, 1)]
    [InlineData(CrossPlatformExport.LINUX, 2)]
    [InlineData(CrossPlatformExport.OSX, 3)]
    public void CrossPlatformExport_HasExpectedIntegerValue(CrossPlatformExport value, int expected)
    {
        ((int)value).Should().Be(expected);
    }

    [Fact]
    public void CrossPlatformExport_HasExactly4Values()
    {
        Enum.GetValues<CrossPlatformExport>().Should().HaveCount(4);
    }
}
