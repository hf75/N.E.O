using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using FluentAssertions;
using Neo.App.Mcp.Internal;
using Xunit;

namespace Neo.App.Mcp.Tests;

/// <summary>
/// Frozen-Mode mirror of the Phase 3 Dev-Mode RoutedEvent-resolution suite. Both code paths
/// must agree, otherwise <c>raise_event</c> behaviour diverges between
/// <c>compile_and_preview</c> sessions and exported single-file EXEs.
/// </summary>
public class InputDispatcherTests
{
    [Fact]
    public void ResolveRoutedEvent_OnButton_FindsClickEventByBothBareAndSuffixedName()
    {
        InputDispatcher.ResolveRoutedEvent(typeof(Button), "Click").Should().BeSameAs(Button.ClickEvent);
        InputDispatcher.ResolveRoutedEvent(typeof(Button), "ClickEvent").Should().BeSameAs(Button.ClickEvent);
    }

    [Fact]
    public void ResolveRoutedEvent_WalksBaseTypes_FindsEventDeclaredOnAncestor()
    {
        InputDispatcher.ResolveRoutedEvent(typeof(ToggleButton), "KeyDown")
            .Should().BeSameAs(InputElement.KeyDownEvent);
    }

    [Fact]
    public void ResolveRoutedEvent_UnknownName_ReturnsNull()
    {
        InputDispatcher.ResolveRoutedEvent(typeof(Button), "NotARealEvent").Should().BeNull();
        InputDispatcher.ResolveRoutedEvent(typeof(Button), "").Should().BeNull();
    }

    [Fact]
    public void ResolveRoutedEvent_OnTextBox_FindsTextInputAcrossInheritance()
    {
        InputDispatcher.ResolveRoutedEvent(typeof(TextBox), "TextInput")
            .Should().BeSameAs(InputElement.TextInputEvent);
    }
}
