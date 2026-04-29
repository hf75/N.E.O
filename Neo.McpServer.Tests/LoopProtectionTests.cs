using FluentAssertions;
using Neo.McpServer.Services;
using Xunit;

namespace Neo.McpServer.Tests;

/// <summary>
/// Phase 1 close-out eval suite: covers the full state-machine of <see cref="LoopProtection"/>,
/// the server-side guard that caps Live-MCP <c>invoke_method</c> ⇄ <c>Ai.Trigger</c> ping-pong.
/// Five cases, one per critical transition — increment, enforcement, decay reset, cross-app
/// seeding, and explicit teardown. Phase 2 (dynamic per-method tools) and Phase 3 (input
/// simulation) will reuse this guard, so a regression here would silently let runaway loops
/// burn through the model budget.
/// </summary>
public class LoopProtectionTests
{
    [Fact]
    public void OnInvokeMethod_FirstCall_ReturnsHopsOne()
    {
        var lp = new LoopProtection();

        lp.OnInvokeMethod("app-a").Should().Be(1);
        lp.OnInvokeMethod("app-a").Should().Be(2);
        lp.OnInvokeMethod("app-a").Should().Be(3);
    }

    [Fact]
    public void OnInvokeMethod_AtMaxDepth_ThrowsLoopLimitExceededException()
    {
        var lp = new LoopProtection();
        var max = lp.MaxDepth;

        for (int i = 0; i < max; i++)
            lp.OnInvokeMethod("app-loop");

        var act = () => lp.OnInvokeMethod("app-loop");

        act.Should().Throw<LoopLimitExceededException>()
            .Where(e => e.AppId == "app-loop"
                     && e.Hops == max + 1
                     && e.MaxDepth == max);
    }

    [Fact]
    public void OnInvokeMethod_AfterDecayWindow_ResetsChain()
    {
        // 30 ms decay vs 300 ms wait gives a 10× safety margin so Windows clock-resolution
        // jitter (15.6 ms typical) and CI scheduling lag don't make this flaky.
        var lp = new LoopProtection(decay: TimeSpan.FromMilliseconds(30));
        lp.OnInvokeMethod("app-decay").Should().Be(1);
        lp.OnInvokeMethod("app-decay").Should().Be(2);

        Thread.Sleep(300);

        // Chain is older than decay → counter resets, this call counts as fresh hops=1.
        lp.OnInvokeMethod("app-decay").Should().Be(1);
    }

    [Fact]
    public void OnAppEvent_SeedsHopsWithMaximum()
    {
        var lp = new LoopProtection();

        // App-side already saw 3 hops before pushing the AppEvent.
        lp.OnAppEvent("app-cross", hopsFromApp: 3);

        // Next invoke continues from 3 → returns 4, NOT 1. Cross-app chains keep their budget.
        lp.OnInvokeMethod("app-cross").Should().Be(4);

        // A lower hop count from a later AppEvent must not lower the counter.
        lp.OnAppEvent("app-cross", hopsFromApp: 1);
        lp.OnInvokeMethod("app-cross").Should().Be(5);
    }

    [Fact]
    public void ResetApp_ClearsChainState()
    {
        var lp = new LoopProtection();
        lp.OnInvokeMethod("app-reset").Should().Be(1);
        lp.OnInvokeMethod("app-reset").Should().Be(2);

        lp.ResetApp("app-reset");

        // After reset the next invoke starts fresh.
        lp.OnInvokeMethod("app-reset").Should().Be(1);
    }
}
