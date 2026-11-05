using System;
using System.Linq;

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace Neo.PluginWindowAvalonia
{
    /// <summary>
    /// A button that shows a panel or a picture (an "A" over a red bar for a font colour) has no name of its own to UI
    /// Automation: Avalonia hands it the type of what it shows, "Avalonia.Controls.StackPanel". A screen reader reads that
    /// out, and nobody can press it by a name, neither N.E.O.'s interfaces nor Claude. Such a button is named by its
    /// tooltip, else by the texts it shows. A name the app gave it, and a text it shows itself, stay as they are.
    /// The tooltip of every button is its help text too: three buttons that all show an "A" (bigger, effects, colour)
    /// differ only there.
    /// </summary>
    public static class ButtonNames
    {
        /// <summary>Names every such button of this process when it is loaded.</summary>
        public static void NameEveryButtonByWhatItShows() =>
            Control.LoadedEvent.Raised.Subscribe(new Avalonia.Reactive.AnonymousObserver<(object, RoutedEventArgs)>(raised =>
            {
                if (raised.Item1 is Button button) NameByWhatItShows(button);
            }));

        public static void NameByWhatItShows(Button button)
        {
            var tip = ToolTip.GetTip(button) as string;
            if (!string.IsNullOrWhiteSpace(tip) && string.IsNullOrEmpty(AutomationProperties.GetHelpText(button)))
                AutomationProperties.SetHelpText(button, tip.Trim());

            if (!string.IsNullOrEmpty(AutomationProperties.GetName(button)) || button.Content is null or string or TextBlock) return;
            var name = tip;
            if (string.IsNullOrWhiteSpace(name) && button.Content is ILogical shown)
                name = string.Join(" ", shown.GetSelfAndLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
            if (!string.IsNullOrWhiteSpace(name)) AutomationProperties.SetName(button, name.Trim());
        }
    }
}
