using FluentAssertions;
using ModelContextProtocol.Server;
using Neo.IPC;
using Neo.McpServer.Services;
using Xunit;

namespace Neo.McpServer.Tests;

/// <summary>
/// Phase 2 close-out: <see cref="LiveMcpToolRegistry"/> covers tool naming, signature hashing,
/// hot-reload dedup, and per-app teardown — the wiring that makes per-method MCP tools
/// (M2.1) and stable hot-reload identity (M2.4) work without spamming
/// <c>notifications/tools/list_changed</c>.
///
/// <para>Note: tests for the actual MCP wire-up (tool collection augmentation, ListChanged
/// firing, schema serialization) live downstream — these focus on the deterministic naming,
/// sanitization, and registry state-machine since that's where regressions silently
/// break Claude's tool resolution.</para>
/// </summary>
public class LiveMcpToolRegistryTests
{
    [Fact]
    public void ToSnakeCase_ConvertsPascalAndCamelToSnake()
    {
        LiveMcpToolRegistry.ToSnakeCase("AddItem").Should().Be("add_item");
        LiveMcpToolRegistry.ToSnakeCase("CompleteItem").Should().Be("complete_item");
        LiveMcpToolRegistry.ToSnakeCase("RefreshFromAPI").Should().Be("refresh_from_api");
        LiveMcpToolRegistry.ToSnakeCase("X").Should().Be("x");
        LiveMcpToolRegistry.ToSnakeCase("count").Should().Be("count");
        LiveMcpToolRegistry.ToSnakeCase("HTTP2Client").Should().Be("http2_client");
    }

    [Fact]
    public void SanitizeAppId_StripsIllegalCharactersAndLowercases()
    {
        LiveMcpToolRegistry.SanitizeAppId("default").Should().Be("default");
        LiveMcpToolRegistry.SanitizeAppId("Window-1").Should().Be("window1");
        LiveMcpToolRegistry.SanitizeAppId("App.Foo/Bar").Should().Be("appfoobar");
        LiveMcpToolRegistry.SanitizeAppId("###").Should().Be("app");
        LiveMcpToolRegistry.SanitizeAppId("snake_case_id").Should().Be("snake_case_id");
    }

    [Fact]
    public void HashCallable_StableAcrossEqualSignatures_DiffersOnSignatureChange()
    {
        var a = new McpCallableEntry(
            Name: "AddItem",
            Description: "ignored for hash",
            Parameters: new List<McpParamEntry> { new("title", "System.String") },
            ReturnTypeName: "System.Int32");

        // Same shape, different description — should hash identically (description doesn't
        // contribute to identity, otherwise every comment edit would re-register).
        var aPrime = a with { Description = "totally different doc" };
        LiveMcpToolRegistry.HashCallable("Cls", a)
            .Should().Be(LiveMcpToolRegistry.HashCallable("Cls", aPrime));

        // Param type change → different hash (signature changed).
        var bDiffType = a with
        {
            Parameters = new List<McpParamEntry> { new("title", "System.Int32") }
        };
        LiveMcpToolRegistry.HashCallable("Cls", bDiffType)
            .Should().NotBe(LiveMcpToolRegistry.HashCallable("Cls", a));

        // Param name change → different hash (Claude sees the rename in its schema).
        var bRenamed = a with
        {
            Parameters = new List<McpParamEntry> { new("text", "System.String") }
        };
        LiveMcpToolRegistry.HashCallable("Cls", bRenamed)
            .Should().NotBe(LiveMcpToolRegistry.HashCallable("Cls", a));

        // Class change → different hash (same method name on different class is a different tool).
        LiveMcpToolRegistry.HashCallable("OtherCls", a)
            .Should().NotBe(LiveMcpToolRegistry.HashCallable("Cls", a));
    }

    [Fact]
    public void RegisterApp_PublishesPerCallableTool_WithExpectedName()
    {
        var registry = new LiveMcpToolRegistry();
        var preview = StubPreviewSessionManager();

        var manifest = ManifestWith(
            classFullName: "MyApp.DynamicUserControl",
            new McpCallableEntry("AddItem", "Adds a TODO.",
                new List<McpParamEntry> { new("title", "System.String") }, "System.Int32"),
            new McpCallableEntry("CompleteItem", "Marks an item done.",
                new List<McpParamEntry> { new("index", "System.Int32") }, "System.Void"));

        registry.RegisterApp("default", manifest, preview);

        registry.GetRegisteredToolNamesForApp("default")
            .Should().BeEquivalentTo("app.default.add_item", "app.default.complete_item");
        registry.GetRegisteredToolNames().Should().HaveCount(2);
    }

    [Fact]
    public void RegisterApp_HotReloadSameSignatures_DoesNotChangeToolInstances()
    {
        var registry = new LiveMcpToolRegistry();
        var preview = StubPreviewSessionManager();

        var manifest = ManifestWith(
            classFullName: "MyApp.DynamicUserControl",
            new McpCallableEntry("AddItem", "first description",
                new List<McpParamEntry> { new("title", "System.String") }, "System.Int32"));

        registry.RegisterApp("default", manifest, preview);
        var toolBefore = registry.ToolCollection["app.default.add_item"];

        // Hot-reload: only description changed. Signature is identical → tool stays put.
        var manifestSameShape = manifest with
        {
            Callables = new List<McpCallableEntry>
            {
                manifest.Callables[0] with { Description = "second description" }
            }
        };
        registry.RegisterApp("default", manifestSameShape, preview);
        var toolAfter = registry.ToolCollection["app.default.add_item"];

        ReferenceEquals(toolBefore, toolAfter).Should().BeTrue(
            "an unchanged signature must NOT trigger remove+add; that would burn a tools/list_changed " +
            "notification on every hot-reload and force Claude to re-fetch the schema for no reason.");
    }

    [Fact]
    public void RegisterApp_SignatureChange_RemovesOldToolAndAddsNew()
    {
        var registry = new LiveMcpToolRegistry();
        var preview = StubPreviewSessionManager();

        registry.RegisterApp("default",
            ManifestWith("Cls", new McpCallableEntry("AddItem", "v1",
                new List<McpParamEntry> { new("title", "System.String") }, "System.Int32")),
            preview);
        var toolBefore = registry.ToolCollection["app.default.add_item"];

        // Same method NAME but param type changed: System.String → System.Int32. Different hash → re-registered.
        registry.RegisterApp("default",
            ManifestWith("Cls", new McpCallableEntry("AddItem", "v2",
                new List<McpParamEntry> { new("title", "System.Int32") }, "System.Int32")),
            preview);
        var toolAfter = registry.ToolCollection["app.default.add_item"];

        ReferenceEquals(toolBefore, toolAfter).Should().BeFalse(
            "a signature change must produce a fresh tool instance so Claude refetches the schema.");
    }

    [Fact]
    public void UnregisterApp_RemovesAllToolsForThatApp_LeavesOtherAppsUntouched()
    {
        var registry = new LiveMcpToolRegistry();
        var preview = StubPreviewSessionManager();

        registry.RegisterApp("alpha",
            ManifestWith("Cls", new McpCallableEntry("AddItem", "",
                new List<McpParamEntry> { new("title", "System.String") }, "System.Void")),
            preview);
        registry.RegisterApp("beta",
            ManifestWith("Cls", new McpCallableEntry("Refresh", "",
                new List<McpParamEntry>(), "System.Void")),
            preview);

        registry.UnregisterApp("alpha");

        registry.GetRegisteredToolNamesForApp("alpha").Should().BeEmpty();
        registry.GetRegisteredToolNamesForApp("beta").Should().ContainSingle()
            .Which.Should().Be("app.beta.refresh");
        registry.GetRegisteredToolNames().Should().ContainSingle();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static AppManifestMessage ManifestWith(string classFullName, params McpCallableEntry[] callables) =>
        new(
            AppId: "default",
            ClassFullName: classFullName,
            Callables: callables.ToList(),
            Observables: new List<McpObservableEntry>(),
            Triggerables: new List<McpTriggerableEntry>());

    /// <summary>
    /// The registry never invokes the preview during register/unregister — it only stores the
    /// reference for later use by the dynamic tools' InvokeAsync. So a default-constructed
    /// instance with no live sessions is good enough for all the registry-level tests above.
    /// </summary>
    private static PreviewSessionManager StubPreviewSessionManager() =>
        new(new LoopProtection(), new LiveMcpToolRegistry(), new LiveMcpResourceRegistry());
}
