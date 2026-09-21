using FluentAssertions;
using Neo.AssemblyForge;
using Xunit;

namespace Neo.HotReload.Tests;

/// <summary>What the prompts tell the models and what the engine recognises must be the same thing.</summary>
public class ConventionTests
{
    [Fact]
    public void Every_ui_method_name_the_prompts_recommend_is_one_the_engine_rebuilds_with()
    {
        StateMigrationAnalyzer.RebuildMethodNames.Should().Contain(HotReloadGuidance.PreferredUiMethod);
        StateMigrationAnalyzer.RebuildMethodNames.Should().Contain("InitializeComponents", "the desktop base code builds its UI there");
    }

    [Fact]
    public async Task The_shape_of_the_mcp_code_template_is_rebuilt_too()
    {
        // create_avalonia_app / create_wpf_app show "Content = BuildUI();" with a method that returns the root.
        const string v1 = """
            public class DynamicUserControl
            {
                public object? Content;
                public DynamicUserControl() { Content = BuildUI(); }
                private object BuildUI() => "ui v1";
            }
            """;
        using var app = await HotReloadHarness.StartAsync(v1);

        var (update, _) = await app.UpdateAsync(v1.Replace("\"ui v1\"", "\"ui v2\""));

        update.Status.Should().Be(HotReloadStatus.Ready, update.Reason);
        update.Request!.RebuildUi.Should().BeTrue();
        update.Request.RebuildMethodName.Should().Be("BuildUI", "names are matched case-insensitively");
        HotReloadHarness.Read(app.Root, "Content").Should().Be("ui v1", "the harness does not assign the returned root; the windows do");
        Neo.Shared.HotReloadRuntime.InvokeRebuild(app.Root, "BuildUI").Should().Be("ui v2");
    }
}
