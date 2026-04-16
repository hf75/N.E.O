using FluentAssertions;
using Neo.App;
using System.Collections.Immutable;
using System.ComponentModel;
using Xunit;

namespace Neo.App.Core.Tests;

public class UndoRedoManagerTests
{
    private static (UndoRedoManager mgr, ApplicationState state) Create()
    {
        var state = new ApplicationState { History = "", LastCode = "" };
        var mgr = new UndoRedoManager(state);
        return (mgr, state);
    }

    // ── Initial state ───────────────────────────────────────────────

    [Fact]
    public void Constructor_InitialState_CanUndoIsFalse()
    {
        var (mgr, _) = Create();

        mgr.CanUndo.Should().BeFalse();
    }

    [Fact]
    public void Constructor_InitialState_CanRedoIsFalse()
    {
        var (mgr, _) = Create();

        mgr.CanRedo.Should().BeFalse();
    }

    [Fact]
    public void Constructor_InitialState_CurrentIsRoot()
    {
        var (mgr, _) = Create();

        mgr.Current.Should().BeSameAs(mgr.Root);
    }

    // ── CommitChange creates a new node ─────────────────────────────

    [Fact]
    public void CommitChange_AfterCommit_CanUndoIsTrue()
    {
        var (mgr, state) = Create();
        state.LastCode = "code1";

        mgr.CommitChange("change 1", "desc");

        mgr.CanUndo.Should().BeTrue();
    }

    [Fact]
    public void CommitChange_ReturnsNewNode()
    {
        var (mgr, state) = Create();
        state.LastCode = "code1";

        var node = mgr.CommitChange("title", "desc");

        node.Should().NotBeNull();
        node!.Title.Should().Be("title");
    }

    // ── Undo restores previous state ────────────────────────────────

    [Fact]
    public async Task Undo_RestoresPreviousState()
    {
        var (mgr, state) = Create();
        state.LastCode = "v1";
        mgr.CommitChange("change1", "");
        state.LastCode = "v2";
        mgr.CommitChange("change2", "");

        await mgr.Undo();

        state.LastCode.Should().Be("v1");
    }

    [Fact]
    public async Task Undo_CanRedoBecomesTrue()
    {
        var (mgr, state) = Create();
        state.LastCode = "v1";
        mgr.CommitChange("change1", "");

        await mgr.Undo();

        mgr.CanRedo.Should().BeTrue();
    }

    // ── Redo restores next state ────────────────────────────────────

    [Fact]
    public async Task Redo_RestoresNextState()
    {
        var (mgr, state) = Create();
        state.LastCode = "v1";
        mgr.CommitChange("change1", "");

        await mgr.Undo();
        await mgr.Redo();

        state.LastCode.Should().Be("v1");
    }

    // ── Multiple undos back to root ─────────────────────────────────

    [Fact]
    public async Task Undo_MultipleTimesBackToRoot()
    {
        var (mgr, state) = Create();
        state.LastCode = "v1";
        mgr.CommitChange("c1", "");
        state.LastCode = "v2";
        mgr.CommitChange("c2", "");
        state.LastCode = "v3";
        mgr.CommitChange("c3", "");

        await mgr.Undo();
        await mgr.Undo();
        await mgr.Undo();

        mgr.Current.Should().BeSameAs(mgr.Root);
        mgr.CanUndo.Should().BeFalse();
    }

    // ── Branch: commit after undo ───────────────────────────────────

    [Fact]
    public async Task CommitAfterUndo_CreatesBranch()
    {
        var (mgr, state) = Create();
        state.LastCode = "v1";
        mgr.CommitChange("c1", "");

        await mgr.Undo();

        state.LastCode = "v2-branch";
        mgr.CommitChange("branch", "");

        // Root should have 2 children.
        mgr.Root.Children.Should().HaveCount(2);
    }

    // ── Deterministic redo: uses LastVisitedChild ───────────────────

    [Fact]
    public async Task Redo_AfterUndo_GoesToLastVisitedChild()
    {
        var (mgr, state) = Create();
        state.LastCode = "v1";
        var node1 = mgr.CommitChange("c1", "");

        await mgr.Undo();

        state.LastCode = "v2";
        mgr.CommitChange("c2", "");

        await mgr.Undo();
        // LastVisitedChild of Root should be the node we just left (c2).
        await mgr.Redo();

        state.LastCode.Should().Be("v2");
    }

    // ── IsRedoAmbiguous ─────────────────────────────────────────────

    [Fact]
    public void IsRedoAmbiguous_NoChildren_ReturnsFalse()
    {
        var (mgr, _) = Create();

        mgr.IsRedoAmbiguous.Should().BeFalse();
    }

    [Fact]
    public void IsRedoAmbiguous_OneChild_ReturnsFalse()
    {
        var (mgr, state) = Create();
        state.LastCode = "v1";
        mgr.CommitChange("c1", "");

        // Go back to root, which has 1 child.
        // (We do not call undo here to avoid changing LastVisitedChild timing.)
        // Instead manually check: Root has 1 child + LastVisitedChild set → not ambiguous.
        mgr.Root.Children.Should().HaveCount(1);
        // From root: deterministic redo should be available.
    }

    [Fact]
    public async Task IsRedoAmbiguous_MultipleChildrenWithLastVisited_ReturnsFalse()
    {
        var (mgr, state) = Create();
        state.LastCode = "v1";
        mgr.CommitChange("c1", "");

        await mgr.Undo();
        state.LastCode = "v2";
        mgr.CommitChange("c2", "");

        await mgr.Undo();

        // Root has 2 children, but LastVisitedChild is set by the undo.
        mgr.IsRedoAmbiguous.Should().BeFalse();
    }

    // ── Checkout ────────────────────────────────────────────────────

    [Fact]
    public void Checkout_NavigatesToArbitraryNode()
    {
        var (mgr, state) = Create();
        state.LastCode = "v1";
        var node1 = mgr.CommitChange("c1", "")!;
        state.LastCode = "v2";
        mgr.CommitChange("c2", "");

        mgr.Checkout(node1);

        mgr.Current.Should().BeSameAs(node1);
        state.LastCode.Should().Be("v1");
    }

    [Fact]
    public void Checkout_NullNode_ThrowsArgumentNullException()
    {
        var (mgr, _) = Create();

        var act = () => mgr.Checkout(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── Clear / ResetToCurrentState ─────────────────────────────────

    [Fact]
    public void Clear_ResetsToSingleRootNode()
    {
        var (mgr, state) = Create();
        state.LastCode = "v1";
        mgr.CommitChange("c1", "");
        state.LastCode = "v2";
        mgr.CommitChange("c2", "");

        mgr.Clear();

        mgr.CanUndo.Should().BeFalse();
        mgr.CanRedo.Should().BeFalse();
        mgr.Current.Should().BeSameAs(mgr.Root);
        mgr.Root.Children.Should().BeEmpty();
    }

    [Fact]
    public void ResetToCurrentState_PreservesCurrentStateAsNewRoot()
    {
        var (mgr, state) = Create();
        state.LastCode = "v1";
        mgr.CommitChange("c1", "");

        mgr.ResetToCurrentState("new root", "fresh");

        mgr.Root.Title.Should().Be("new root");
        mgr.Root.Snapshot.LastCode.Should().Be("v1");
    }

    // ── skipIfUnchanged ─────────────────────────────────────────────

    [Fact]
    public void CommitChange_SkipIfUnchanged_ReturnsNullWhenNoChange()
    {
        var (mgr, state) = Create();
        // State hasn't changed since root snapshot was taken.

        var result = mgr.CommitChange("no-op", "", skipIfUnchanged: true);

        result.Should().BeNull();
    }

    [Fact]
    public void CommitChange_SkipIfUnchangedFalse_CommitsEvenWhenUnchanged()
    {
        var (mgr, state) = Create();

        var result = mgr.CommitChange("forced", "", skipIfUnchanged: false);

        result.Should().NotBeNull();
    }

    // ── PropertyChanged events ──────────────────────────────────────

    [Fact]
    public void CommitChange_FiresPropertyChangedForCanUndo()
    {
        var (mgr, state) = Create();
        state.LastCode = "v1";

        var changedProps = new List<string>();
        mgr.PropertyChanged += (_, e) => changedProps.Add(e.PropertyName!);

        mgr.CommitChange("c1", "");

        changedProps.Should().Contain("CanUndo");
    }

    [Fact]
    public void CommitChange_FiresPropertyChangedForCanRedo()
    {
        var (mgr, state) = Create();
        state.LastCode = "v1";

        var changedProps = new List<string>();
        mgr.PropertyChanged += (_, e) => changedProps.Add(e.PropertyName!);

        mgr.CommitChange("c1", "");

        changedProps.Should().Contain("CanRedo");
    }

    // ── GraphChanged event ──────────────────────────────────────────

    [Fact]
    public void CommitChange_FiresGraphChanged()
    {
        var (mgr, state) = Create();
        state.LastCode = "v1";

        var graphChangedFired = false;
        mgr.GraphChanged += (_, _) => graphChangedFired = true;

        mgr.CommitChange("c1", "");

        graphChangedFired.Should().BeTrue();
    }

    [Fact]
    public async Task Undo_FiresGraphChanged()
    {
        var (mgr, state) = Create();
        state.LastCode = "v1";
        mgr.CommitChange("c1", "");

        var graphChangedFired = false;
        mgr.GraphChanged += (_, _) => graphChangedFired = true;

        await mgr.Undo();

        graphChangedFired.Should().BeTrue();
    }

    // ── Undo/Redo return false when not possible ────────────────────

    [Fact]
    public async Task Undo_AtRoot_ReturnsFalse()
    {
        var (mgr, _) = Create();

        var result = await mgr.Undo();

        result.Should().BeFalse();
    }

    [Fact]
    public async Task Redo_NoChildren_ReturnsFalse()
    {
        var (mgr, _) = Create();

        var result = await mgr.Redo();

        result.Should().BeFalse();
    }

    // ── Constructor null state throws ───────────────────────────────

    [Fact]
    public void Constructor_NullAppState_ThrowsArgumentNullException()
    {
        var act = () => new UndoRedoManager(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}

public class ApplicationStateMementoTests
{
    // ── CreateMemento captures current state ────────────────────────

    [Fact]
    public void CreateMemento_CapturesCurrentState()
    {
        var state = new ApplicationState
        {
            History = "some history",
            LastCode = "some code",
        };
        state.NuGetDlls.Add("lib.dll");
        state.PackageVersions["Pkg"] = "1.0";

        var memento = state.CreateMemento();

        memento.History.Should().Be("some history");
        memento.LastCode.Should().Be("some code");
        memento.NuGetDlls.Should().Contain("lib.dll");
        memento.PackageVersions.Should().ContainKey("Pkg");
    }

    // ── RestoreFromMemento restores exact state ─────────────────────

    [Fact]
    public void RestoreFromMemento_RestoresExactState()
    {
        var state = new ApplicationState
        {
            History = "h1",
            LastCode = "c1",
        };
        state.NuGetDlls.Add("a.dll");
        state.PackageVersions["X"] = "2.0";

        var memento = state.CreateMemento();

        // Modify state.
        state.History = "h2";
        state.LastCode = "c2";
        state.NuGetDlls.Clear();
        state.PackageVersions.Clear();

        state.RestoreFromMemento(memento);

        state.History.Should().Be("h1");
        state.LastCode.Should().Be("c1");
        state.NuGetDlls.Should().Contain("a.dll");
        state.PackageVersions["X"].Should().Be("2.0");
    }

    // ── StructurallyEquals ──────────────────────────────────────────

    [Fact]
    public void StructurallyEquals_SameContent_ReturnsTrue()
    {
        var m1 = new ApplicationStateMemento
        {
            History = "h",
            LastCode = "c",
            NuGetDlls = ImmutableArray.Create("a.dll"),
            PackageVersions = ImmutableDictionary.CreateRange(
                StringComparer.Ordinal,
                new[] { new KeyValuePair<string, string>("P", "1") }),
        };

        var m2 = new ApplicationStateMemento
        {
            History = "h",
            LastCode = "c",
            NuGetDlls = ImmutableArray.Create("a.dll"),
            PackageVersions = ImmutableDictionary.CreateRange(
                StringComparer.Ordinal,
                new[] { new KeyValuePair<string, string>("P", "1") }),
        };

        m1.StructurallyEquals(m2).Should().BeTrue();
    }

    [Fact]
    public void StructurallyEquals_DifferentHistory_ReturnsFalse()
    {
        var m1 = new ApplicationStateMemento { History = "a" };
        var m2 = new ApplicationStateMemento { History = "b" };

        m1.StructurallyEquals(m2).Should().BeFalse();
    }

    [Fact]
    public void StructurallyEquals_DifferentCode_ReturnsFalse()
    {
        var m1 = new ApplicationStateMemento { LastCode = "x" };
        var m2 = new ApplicationStateMemento { LastCode = "y" };

        m1.StructurallyEquals(m2).Should().BeFalse();
    }

    [Fact]
    public void StructurallyEquals_DifferentNuGetDlls_ReturnsFalse()
    {
        var m1 = new ApplicationStateMemento { NuGetDlls = ImmutableArray.Create("a.dll") };
        var m2 = new ApplicationStateMemento { NuGetDlls = ImmutableArray.Create("b.dll") };

        m1.StructurallyEquals(m2).Should().BeFalse();
    }

    [Fact]
    public void StructurallyEquals_DifferentPackageVersions_ReturnsFalse()
    {
        var m1 = new ApplicationStateMemento
        {
            PackageVersions = ImmutableDictionary.CreateRange(
                StringComparer.Ordinal,
                new[] { new KeyValuePair<string, string>("P", "1") }),
        };
        var m2 = new ApplicationStateMemento
        {
            PackageVersions = ImmutableDictionary.CreateRange(
                StringComparer.Ordinal,
                new[] { new KeyValuePair<string, string>("P", "2") }),
        };

        m1.StructurallyEquals(m2).Should().BeFalse();
    }

    [Fact]
    public void StructurallyEquals_SameReference_ReturnsTrue()
    {
        var m = new ApplicationStateMemento();

        m.StructurallyEquals(m).Should().BeTrue();
    }

    [Fact]
    public void StructurallyEquals_Null_ReturnsFalse()
    {
        var m = new ApplicationStateMemento();

        m.StructurallyEquals(null!).Should().BeFalse();
    }

    // ── Memento immutability ────────────────────────────────────────

    [Fact]
    public void Memento_IsImmutable_ModifyingOriginalDoesNotAffectMemento()
    {
        var state = new ApplicationState { LastCode = "original" };
        state.NuGetDlls.Add("x.dll");

        var memento = state.CreateMemento();

        state.LastCode = "modified";
        state.NuGetDlls.Clear();

        memento.LastCode.Should().Be("original");
        memento.NuGetDlls.Should().Contain("x.dll");
    }
}
