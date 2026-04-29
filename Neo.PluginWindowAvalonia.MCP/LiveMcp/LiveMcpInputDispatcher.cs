using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Neo.IPC;

namespace Neo.PluginWindowAvalonia.MCP.LiveMcp;

/// <summary>
/// Phase 3 UI-driving runtime: handles <c>RaiseEvent</c> and <c>SimulateInput</c> IPC frames
/// from the host, finds the target control by name in the loaded UserControl's visual tree,
/// and exercises the appropriate Avalonia code path.
///
/// <para>All operations marshal to the UI thread via <see cref="Dispatcher.UIThread"/>; the
/// caller (<see cref="Neo.PluginWindowAvalonia.MCP.App"/>) lives on the IPC pipe thread.</para>
///
/// <para>Strategy mix per VISION.md §3:
/// <list type="bullet">
///   <item><b>Semantic</b> for <c>focus</c>, <c>set_text</c> — direct Avalonia API call.</item>
///   <item><b>Event</b> for <c>click</c>, <c>key_press</c>, <c>type_text</c>, and all of <c>RaiseEvent</c> —
///         <see cref="Interactive.RaiseEvent"/> through the routing pipeline so bubbling/tunneling
///         hits user handlers exactly as a real interaction would.</item>
///   <item>Real <see cref="InputManager"/>-driven input (mouse coords, hover, focus chain) is
///         reserved for a follow-up — most self-testing scenarios don't need it.</item>
/// </list></para>
/// </summary>
internal sealed class LiveMcpInputDispatcher
{
    private readonly Func<Control?> _getUserControl;

    public LiveMcpInputDispatcher(Func<Control?> getUserControl)
    {
        _getUserControl = getUserControl;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // RaiseEvent
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<RaiseEventResultMessage> RaiseEventAsync(RaiseEventMessage frame)
    {
        var (control, error) = await ResolveControlAsync(frame.ControlName);
        if (control == null) return new RaiseEventResultMessage(false, error, "control_not_found");

        var routed = ResolveRoutedEvent(control.GetType(), frame.EventName);
        if (routed == null)
            return new RaiseEventResultMessage(false,
                $"RoutedEvent '{frame.EventName}' not found on '{control.GetType().Name}'.",
                "event_not_found");

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => control.RaiseEvent(new RoutedEventArgs(routed) { Source = control }));
            return new RaiseEventResultMessage(true);
        }
        catch (Exception ex)
        {
            return new RaiseEventResultMessage(false, $"{ex.GetType().Name}: {ex.Message}", "invocation_failed");
        }
    }

    /// <summary>
    /// Look up a static <see cref="RoutedEvent"/> field by name on the control's type or any base type.
    /// Accepts either the bare name (<c>Click</c>) or the conventional suffixed form (<c>ClickEvent</c>).
    /// </summary>
    internal static RoutedEvent? ResolveRoutedEvent(Type controlType, string eventName)
    {
        if (string.IsNullOrEmpty(eventName)) return null;
        var candidates = new[] { eventName, eventName + "Event" }.Distinct();

        for (var t = controlType; t != null; t = t.BaseType)
        {
            foreach (var candidate in candidates)
            {
                var field = t.GetField(candidate, BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
                if (field?.GetValue(null) is RoutedEvent re) return re;
            }
        }
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SimulateInput
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<SimulateInputResultMessage> SimulateInputAsync(SimulateInputMessage frame)
    {
        var (control, error) = await ResolveControlAsync(frame.ControlName);
        if (control == null) return new SimulateInputResultMessage(false, error, "control_not_found");

        try
        {
            return frame.Kind switch
            {
                "click"     => await ClickAsync(control),
                "focus"     => await FocusAsync(control),
                "set_text"  => await SetTextAsync(control, ParseText(frame.ParamsJson)),
                "key_press" => await KeyPressAsync(control, ParseKey(frame.ParamsJson)),
                "type_text" => await TypeTextAsync(control, ParseText(frame.ParamsJson)),
                _ => new SimulateInputResultMessage(false,
                    $"Kind '{frame.Kind}' not supported. " +
                    "Use click / focus / set_text / key_press / type_text.",
                    "kind_not_supported")
            };
        }
        catch (Exception ex)
        {
            return new SimulateInputResultMessage(false, $"{ex.GetType().Name}: {ex.Message}", "invocation_failed");
        }
    }

    private async Task<SimulateInputResultMessage> ClickAsync(Control control)
    {
        // Button.OnClick is the semantic call site; ToggleButton overrides it. For non-Button
        // controls, fall back to RaiseEvent(Click). PointerReleased/Pressed events are
        // intentionally NOT raised here — they require pointer state we don't synthesize in v1.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (control is Button btn)
            {
                // The same code path Avalonia walks when a real pointer release fires Click.
                btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent) { Source = btn });
                return;
            }

            var routed = ResolveRoutedEvent(control.GetType(), "Click");
            if (routed != null)
                control.RaiseEvent(new RoutedEventArgs(routed) { Source = control });
            else
                throw new InvalidOperationException(
                    $"Control type '{control.GetType().Name}' has no Click event; use raise_event with the actual event name.");
        });
        return new SimulateInputResultMessage(true);
    }

    private async Task<SimulateInputResultMessage> FocusAsync(Control control)
    {
        await Dispatcher.UIThread.InvokeAsync(() => control.Focus());
        return new SimulateInputResultMessage(true);
    }

    private async Task<SimulateInputResultMessage> SetTextAsync(Control control, string? text)
    {
        if (text == null)
            return new SimulateInputResultMessage(false, "Missing 'text' parameter.", "invocation_failed");

        // Try common text-bearing properties: Text, Content. Both are settable on most input
        // and label controls. Reflection — not pattern-match — so it works on user-defined
        // UserControls that expose a Text property too.
        var prop = control.GetType().GetProperty("Text", BindingFlags.Public | BindingFlags.Instance)
                   ?? control.GetType().GetProperty("Content", BindingFlags.Public | BindingFlags.Instance);
        if (prop == null || !prop.CanWrite)
            return new SimulateInputResultMessage(false,
                $"Control '{control.GetType().Name}' has no settable Text or Content property.",
                "invocation_failed");

        await Dispatcher.UIThread.InvokeAsync(() => prop.SetValue(control, text));
        return new SimulateInputResultMessage(true);
    }

    private async Task<SimulateInputResultMessage> KeyPressAsync(Control control, Key? key)
    {
        if (key == null)
            return new SimulateInputResultMessage(false,
                "Missing or invalid 'key' parameter (use an Avalonia.Input.Key name like 'Enter', 'A', 'F5').",
                "invocation_failed");

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var down = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key.Value, Source = control };
            control.RaiseEvent(down);
            var up = new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key.Value, Source = control };
            control.RaiseEvent(up);
        });
        return new SimulateInputResultMessage(true);
    }

    private async Task<SimulateInputResultMessage> TypeTextAsync(Control control, string? text)
    {
        if (text == null)
            return new SimulateInputResultMessage(false, "Missing 'text' parameter.", "invocation_failed");

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // One TextInputEvent per character so handlers see the same per-keystroke flow as a
            // real keyboard. Avalonia's TextBox accumulates these into the bound Text property.
            foreach (var ch in text)
            {
                var args = new TextInputEventArgs
                {
                    RoutedEvent = InputElement.TextInputEvent,
                    Text = ch.ToString(),
                    Source = control
                };
                control.RaiseEvent(args);
            }
        });
        return new SimulateInputResultMessage(true);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<(Control? control, string? error)> ResolveControlAsync(string controlName)
    {
        if (string.IsNullOrEmpty(controlName))
            return (null, "Empty control name.");

        var root = await Dispatcher.UIThread.InvokeAsync(() => _getUserControl());
        if (root == null) return (null, "No UserControl is currently loaded.");

        var match = await Dispatcher.UIThread.InvokeAsync(() => FindControlInTree(root, controlName));
        return match == null
            ? (null, $"Control '{controlName}' not found in visual tree.")
            : (match, null);
    }

    /// <summary>
    /// Visual-tree walk identical in spirit to the existing <c>FindControlInTree</c> on
    /// <see cref="Neo.PluginWindowAvalonia.MCP.App"/> — kept here so this dispatcher has no
    /// hidden dependency on the App class.
    /// </summary>
    internal static Control? FindControlInTree(Control root, string target)
    {
        var all = new List<Control>();
        Collect(root, all);

        var byName = all.FirstOrDefault(c => string.Equals(c.Name, target, StringComparison.OrdinalIgnoreCase));
        if (byName != null) return byName;

        var parts = target.Split(':', 2);
        var typeName = parts[0];
        int index = parts.Length > 1 && int.TryParse(parts[1], out var idx) ? idx : 0;
        var byType = all.Where(c => c.GetType().Name.Equals(typeName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (index < byType.Count) return byType[index];
        if (byType.Count > 0) return byType[0];

        var partial = all.FirstOrDefault(c => c.Name != null && c.Name.Contains(target, StringComparison.OrdinalIgnoreCase));
        return partial;

        static void Collect(Visual v, List<Control> result)
        {
            if (v is Control c) result.Add(c);
            foreach (var child in v.GetVisualChildren())
                if (child is Visual vc) Collect(vc, result);
        }
    }

    private static string? ParseText(string paramsJson)
    {
        if (string.IsNullOrWhiteSpace(paramsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(paramsJson);
            return doc.RootElement.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
        }
        catch { return null; }
    }

    private static Key? ParseKey(string paramsJson)
    {
        if (string.IsNullOrWhiteSpace(paramsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(paramsJson);
            if (!doc.RootElement.TryGetProperty("key", out var k) || k.ValueKind != JsonValueKind.String)
                return null;
            var name = k.GetString();
            return Enum.TryParse<Key>(name, ignoreCase: true, out var result) ? result : null;
        }
        catch { return null; }
    }
}
