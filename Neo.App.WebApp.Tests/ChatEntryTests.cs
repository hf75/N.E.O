using System.ComponentModel;
using Neo.App.WebApp.Services.Sessions;

namespace Neo.App.WebApp.Tests;

public class ChatEntryTests
{
    [Fact]
    public void DisplayText_FallsBackToContent()
    {
        var e = new ChatEntry { Content = "hello" };
        Assert.Equal("hello", e.DisplayText);
    }

    [Fact]
    public void DisplayText_OverridesContent()
    {
        var e = new ChatEntry { Content = "{\"code\":\"...\"}", DisplayText = "code updated" };
        Assert.Equal("code updated", e.DisplayText);
    }

    [Fact]
    public void PropertyChanged_FiresForContentAndDisplayText()
    {
        var e = new ChatEntry();
        var changes = new System.Collections.Generic.List<string?>();
        ((INotifyPropertyChanged)e).PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        e.Content = "first";
        Assert.Contains(nameof(ChatEntry.Content), changes);
        Assert.Contains(nameof(ChatEntry.DisplayText), changes); // fallback invalidates too

        changes.Clear();
        e.DisplayText = "override";
        Assert.Contains(nameof(ChatEntry.DisplayText), changes);
    }

    [Fact]
    public void SettingSameValue_DoesNotFire()
    {
        var e = new ChatEntry { Content = "same" };
        var fired = false;
        ((INotifyPropertyChanged)e).PropertyChanged += (_, _) => fired = true;
        e.Content = "same";
        Assert.False(fired);
    }
}
