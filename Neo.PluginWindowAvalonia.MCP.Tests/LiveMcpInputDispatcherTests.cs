using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using FluentAssertions;
using Neo.PluginWindowAvalonia.MCP.LiveMcp;
using Xunit;

namespace Neo.PluginWindowAvalonia.MCP.Tests;

/// <summary>
/// Phase 3 reflection logic for resolving Avalonia <see cref="RoutedEvent"/>s by name across
/// the inheritance chain. The two non-trivial bits to pin down are
/// (a) accepting either the bare name <c>Click</c> or the suffixed conventional form <c>ClickEvent</c>,
/// (b) walking base classes so events declared on <see cref="Button"/> / <see cref="InputElement"/>
/// resolve when the lookup type is a more derived control.
///
/// <para>Full <c>RaiseEvent</c> / <c>SimulateInput</c> behavior — UI-thread dispatch, click event
/// firing, key-press round-trip — needs an Avalonia headless host or a live preview window.
/// Verified via E2E after CLI restart; this suite covers the deterministic reflection step that
/// drives every dispatch.</para>
/// </summary>
public class LiveMcpInputDispatcherTests
{
    [Fact]
    public void ResolveRoutedEvent_OnButton_FindsClickEventByBothBareAndSuffixedName()
    {
        var click1 = LiveMcpInputDispatcher.ResolveRoutedEvent(typeof(Button), "Click");
        var click2 = LiveMcpInputDispatcher.ResolveRoutedEvent(typeof(Button), "ClickEvent");

        click1.Should().BeSameAs(Button.ClickEvent);
        click2.Should().BeSameAs(Button.ClickEvent);
    }

    [Fact]
    public void ResolveRoutedEvent_WalksBaseTypes_FindsEventDeclaredOnAncestor()
    {
        // KeyDown is declared on InputElement. A ToggleButton (subclass of Button) must still resolve it.
        var keyDown = LiveMcpInputDispatcher.ResolveRoutedEvent(typeof(ToggleButton), "KeyDown");

        keyDown.Should().BeSameAs(InputElement.KeyDownEvent);
    }

    [Fact]
    public void ResolveRoutedEvent_UnknownName_ReturnsNull()
    {
        LiveMcpInputDispatcher.ResolveRoutedEvent(typeof(Button), "NotARealEvent")
            .Should().BeNull();
        LiveMcpInputDispatcher.ResolveRoutedEvent(typeof(Button), "")
            .Should().BeNull();
    }

    [Fact]
    public void ResolveRoutedEvent_OnTextBox_FindsTextInputAcrossInheritance()
    {
        // TextInput is on InputElement; TextBox derives from TemplatedControl → Control → InputElement.
        var textInput = LiveMcpInputDispatcher.ResolveRoutedEvent(typeof(TextBox), "TextInput");

        textInput.Should().BeSameAs(InputElement.TextInputEvent);
    }
}
