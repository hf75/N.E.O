using System;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Neo.App.WebApp.Views;

namespace Neo.App.WebApp.Browser;

/// <summary>
/// Wires Monaco into the MainView. On Browser we mount a JS-managed overlay
/// on top of the Avalonia canvas; on Desktop it would be a no-op.
/// </summary>
[SupportedOSPlatform("browser")]
internal static class MonacoWiring
{
    public static void Attach(MainView view)
    {
        var tabs = view.FindControl<TabControl>("RightTabs");
        var host = view.FindControl<Border>("MonacoHost");
        var codeBox = view.FindControl<TextBox>("CodeBox");
        var syncBtn = view.FindControl<Button>("MonacoSyncBtn");
        var applyBtn = view.FindControl<Button>("MonacoApplyBtn");
        if (tabs is null || host is null || codeBox is null || applyBtn is null || syncBtn is null) return;

        view.MonacoCodeProvider = () => MonacoBridge.GetCode();
        view.MonacoCodeSetter = code => MonacoBridge.SetCode(code ?? "");

        bool monacoActive = false;

        async Task ShowAsync()
        {
            await MonacoBridge.EnsureImportedAsync();
            var (x, y, w, h) = MeasureHost(host);
            await MonacoBridge.ShowAsync(x, y, w, h, codeBox.Text ?? "");
            monacoActive = true;
        }

        void Hide()
        {
            MonacoBridge.Hide();
            monacoActive = false;
        }

        tabs.SelectionChanged += (_, _) =>
        {
            var isMonaco = tabs.SelectedItem is TabItem t
                           && t.Header as string == "Monaco";
            if (isMonaco) _ = ShowAsync();
            else Hide();
        };

        // Reposition on any layout change while Monaco is active.
        host.LayoutUpdated += (_, _) =>
        {
            if (!monacoActive) return;
            var (x, y, w, h) = MeasureHost(host);
            if (w > 0 && h > 0) MonacoBridge.Reposition(x, y, w, h);
        };

        syncBtn.Click += async (_, _) =>
        {
            await MonacoBridge.EnsureImportedAsync();
            MonacoBridge.SetCode(codeBox.Text ?? "");
        };

        applyBtn.Click += async (_, _) =>
        {
            await MonacoBridge.EnsureImportedAsync();
            var code = MonacoBridge.GetCode();
            if (!string.IsNullOrWhiteSpace(code))
            {
                codeBox.Text = code;
                // Reuse the existing "Recompile" wiring on the Code tab.
                view.FindControl<Button>("ApplyMonacoBtn")?.RaiseEvent(
                    new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            }
        };

        // Before unload, tear Monaco down so we don't leak the overlay.
        Avalonia.Controls.TopLevel.GetTopLevel(view)?.AddHandler(TopLevel.UnloadedEvent, (_, _) =>
        {
            try { MonacoBridge.Dispose(); } catch { }
        });
    }

    private static (double x, double y, double w, double h) MeasureHost(Control host)
    {
        if (host.GetVisualRoot() is not Visual root) return (0, 0, 0, 0);
        var topLeft = host.TranslatePoint(new Avalonia.Point(0, 0), root) ?? new Avalonia.Point(0, 0);
        return (topLeft.X, topLeft.Y, host.Bounds.Width, host.Bounds.Height);
    }
}
