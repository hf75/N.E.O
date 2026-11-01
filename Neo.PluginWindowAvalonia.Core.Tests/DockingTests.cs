using FluentAssertions;
using Neo.IPC;
using Xunit;

namespace Neo.PluginWindowAvalonia.Core.Tests;

/// <summary>
/// The app window docks to the right of N.E.O. The host reports its position in screen pixels and its size
/// in DIPs, so at 125 % adding the two directly put the app window 150 pixels into N.E.O.
/// </summary>
public class DockingTests
{
    [Theory]
    [InlineData(1.0, 864)]   // 256 + 600 + 8
    [InlineData(1.25, 1016)] // 256 + 750 + 10
    [InlineData(1.5, 1168)]  // 256 + 900 + 12
    [InlineData(2.0, 1472)]  // 256 + 1200 + 16
    public void The_docked_window_starts_right_of_the_host_in_screen_pixels(double scaling, int expectedX)
    {
        new ParentWindowBoundsMessage(X: 256, Y: 100, Width: 600, Height: 768, IsVisible: true)
            .RightDockX(scaling).Should().Be(expectedX);
    }
}
