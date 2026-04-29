using System.Collections.ObjectModel;
using Avalonia.Controls;
using FluentAssertions;
using Neo.App;
using Neo.App.Mcp.Internal;
using Xunit;

namespace Neo.App.Mcp.Tests;

/// <summary>
/// Pin Frozen-Mode's reflection-based manifest construction. The builder does its own
/// FullName-based attribute lookup (no hard dependency on Neo.App.Api), so the tests use a
/// fake UserControl type decorated with the real attributes from Neo.App.Api.
///
/// <para>Cross-mode consistency check: a method or property with the same shape must produce
/// the same manifest entry whether built by the Dev-Mode (<c>LiveMcpManifestBuilder</c>) or
/// the Frozen-Mode builder. The two are independent files for now (see Phase 4 commit) — this
/// test exists so any divergence on either side surfaces immediately.</para>
/// </summary>
public class AppManifestBuilderTests
{
    [Fact]
    public void Build_PicksUpCallableObservableTriggerable_WithExpectedMetadata()
    {
        var control = new SampleControl();

        var manifest = AppManifestBuilder.Build(control);

        manifest.ClassFullName.Should().Be(typeof(SampleControl).FullName);

        manifest.Callables.Should().ContainSingle(c => c.Name == "AddItem")
            .Which.Should().BeEquivalentTo(new
            {
                Name = "AddItem",
                Description = "Adds a TODO item.",
                ReturnTypeName = "System.Int32",
                OffUiThread = false,
                TimeoutSeconds = 30
            });

        manifest.Callables.Single(c => c.Name == "AddItem").Parameters.Should().HaveCount(1)
            .And.ContainSingle(p => p.Name == "title" && p.TypeName == "System.String");

        manifest.Callables.Should().ContainSingle(c => c.Name == "RefreshFromApi")
            .Which.Should().BeEquivalentTo(new
            {
                Name = "RefreshFromApi",
                OffUiThread = true,
                TimeoutSeconds = 60
            }, opts => opts.Including(x => x.Name).Including(x => x.OffUiThread).Including(x => x.TimeoutSeconds));

        manifest.Observables.Should().ContainSingle(o => o.Name == "ItemCount")
            .Which.Watchable.Should().BeFalse();
        manifest.Observables.Should().ContainSingle(o => o.Name == "CompletedCount")
            .Which.Watchable.Should().BeTrue();
    }

    [Fact]
    public void Build_IgnoresUnannotatedMembers()
    {
        var manifest = AppManifestBuilder.Build(new SampleControl());

        // SampleControl has unannotated members that must NOT leak into the manifest.
        manifest.Callables.Should().NotContain(c => c.Name == "InternalHelper");
        manifest.Observables.Should().NotContain(o => o.Name == "InternalState");
    }

    /// <summary>Fake UserControl with the full attribute mix exercised by Phase 1 + 2.</summary>
    private sealed class SampleControl : UserControl
    {
        public ObservableCollection<string> Items { get; } = new();

        [McpObservable("Total number of items.")]
        public int ItemCount => Items.Count;

        [McpObservable("Number of completed items.", Watchable = true)]
        public int CompletedCount => 0;

        [McpCallable("Adds a TODO item.")]
        public int AddItem(string title) => Items.Count;

        [McpCallable("Refreshes data from the API.", OffUiThread = true, TimeoutSeconds = 60)]
        public Task<int> RefreshFromApi() => Task.FromResult(0);

        // Un-annotated — must be invisible to the manifest.
        public string InternalState => "secret";
        public void InternalHelper() { }
    }
}
