using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Neo.App.Mcp.Internal;

/// <summary>
/// Frozen-Mode counterpart to <c>LiveMcpInputDispatcher</c>: drives Avalonia events and
/// synthesizes input on named controls in the loaded UserControl. Logic mirrors the Dev-Mode
/// version so user-side handlers see identical event flow whether they run in a sandboxed
/// preview or an exported single-file app.
/// </summary>
internal sealed class InputDispatcher
{
    private readonly Control _userControl;

    public InputDispatcher(Control userControl)
    {
        _userControl = userControl;
    }

    public async Task<DispatchResult> RaiseEventAsync(string controlName, string eventName)
    {
        var control = await ResolveAsync(controlName);
        if (control == null) return DispatchResult.Fail($"Control '{controlName}' not found in visual tree.");

        var routed = ResolveRoutedEvent(control.GetType(), eventName);
        if (routed == null)
            return DispatchResult.Fail(
                $"RoutedEvent '{eventName}' not found on '{control.GetType().Name}' or its base types.");

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => control.RaiseEvent(new RoutedEventArgs(routed) { Source = control }));
            return DispatchResult.Ok(null);
        }
        catch (Exception ex) { return DispatchResult.Fail($"{ex.GetType().Name}: {ex.Message}"); }
    }

    public async Task<DispatchResult> SimulateInputAsync(string controlName, string kind, string paramsJson)
    {
        var control = await ResolveAsync(controlName);
        if (control == null) return DispatchResult.Fail($"Control '{controlName}' not found in visual tree.");

        try
        {
            return kind switch
            {
                "click"     => await ClickAsync(control),
                "focus"     => await FocusAsync(control),
                "set_text"  => await SetTextAsync(control, ParseText(paramsJson)),
                "key_press" => await KeyPressAsync(control, ParseKey(paramsJson)),
                "type_text" => await TypeTextAsync(control, ParseText(paramsJson)),
                _ => DispatchResult.Fail(
                    $"Kind '{kind}' not supported. Use click / focus / set_text / key_press / type_text.")
            };
        }
        catch (Exception ex) { return DispatchResult.Fail($"{ex.GetType().Name}: {ex.Message}"); }
    }

    internal static RoutedEvent? ResolveRoutedEvent(Type controlType, string eventName)
    {
        if (string.IsNullOrEmpty(eventName)) return null;
        var candidates = new[] { eventName, eventName + "Event" }.Distinct();

        for (var t = controlType; t != null; t = t.BaseType)
        {
            foreach (var c in candidates)
            {
                var field = t.GetField(c, BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
                if (field?.GetValue(null) is RoutedEvent re) return re;
            }
        }
        return null;
    }

    private async Task<Control?> ResolveAsync(string controlName) =>
        string.IsNullOrEmpty(controlName)
            ? null
            : await Dispatcher.UIThread.InvokeAsync(() => VisualTreeFinder.Find(_userControl, controlName));

    private static async Task<DispatchResult> ClickAsync(Control control)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (control is Button btn)
            {
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
        return DispatchResult.Ok(null);
    }

    private static async Task<DispatchResult> FocusAsync(Control control)
    {
        await Dispatcher.UIThread.InvokeAsync(() => control.Focus());
        return DispatchResult.Ok(null);
    }

    private static async Task<DispatchResult> SetTextAsync(Control control, string? text)
    {
        if (text == null) return DispatchResult.Fail("Missing 'text' parameter.");
        var prop = control.GetType().GetProperty("Text", BindingFlags.Public | BindingFlags.Instance)
                   ?? control.GetType().GetProperty("Content", BindingFlags.Public | BindingFlags.Instance);
        if (prop == null || !prop.CanWrite)
            return DispatchResult.Fail($"Control '{control.GetType().Name}' has no settable Text or Content property.");

        await Dispatcher.UIThread.InvokeAsync(() => prop.SetValue(control, text));
        return DispatchResult.Ok(null);
    }

    private static async Task<DispatchResult> KeyPressAsync(Control control, Key? key)
    {
        if (key == null) return DispatchResult.Fail(
            "Missing or invalid 'key' parameter (use an Avalonia.Input.Key name like 'Enter', 'A', 'F5').");

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key.Value, Source = control });
            control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key.Value, Source = control });
        });
        return DispatchResult.Ok(null);
    }

    private static async Task<DispatchResult> TypeTextAsync(Control control, string? text)
    {
        if (text == null) return DispatchResult.Fail("Missing 'text' parameter.");

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var ch in text)
            {
                control.RaiseEvent(new TextInputEventArgs
                {
                    RoutedEvent = InputElement.TextInputEvent,
                    Text = ch.ToString(),
                    Source = control
                });
            }
        });
        return DispatchResult.Ok(null);
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
            if (!doc.RootElement.TryGetProperty("key", out var k) || k.ValueKind != JsonValueKind.String) return null;
            var name = k.GetString();
            return Enum.TryParse<Key>(name, ignoreCase: true, out var result) ? result : null;
        }
        catch { return null; }
    }
}
