using System;
using System.Reflection;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

using Neo.App;
using Neo.IPC;
using Neo.Shared;

namespace Neo.PluginWindowAvalonia
{
    /// <summary>
    /// Watches one thing of the running app for an app link: a property of a control, a field, or a button.
    /// The app may build its UI anew at any time, or be replaced: then the watcher finds the control again by
    /// its address, and reports the value only if it differs. On the UI thread.
    /// </summary>
    internal sealed class LinkWatcher
    {
        // A control can be gone for a moment while the app builds its UI anew.
        private static readonly TimeSpan GoneFor = TimeSpan.FromSeconds(2);

        private readonly MainWindow _window;
        private readonly LinkWatchKind _kind;
        private readonly string _address;
        private readonly string? _property;
        private readonly Func<Control, string, bool> _doneByTheLink;
        private readonly Action<string?> _changed;
        private readonly Action<string> _problem;

        private Control? _control;
        private AvaloniaProperty? _watched;
        private PropertyInfo? _polled;
        private Type? _appType;
        private MemberInfo? _member;
        private string? _last;
        private DateTime? _missingSince;
        private bool _beginning;
        private bool _complained;

        /// <param name="doneByTheLink">True while the link itself changes this: a value that came from the other side does not go back there.</param>
        /// <param name="changed">The new value as JSON; null for a button.</param>
        public LinkWatcher(MainWindow window, LinkWatchKind kind, string address, string? property,
            Func<Control, string, bool> doneByTheLink, Action<string?> changed, Action<string> problem)
        {
            _window = window;
            _kind = kind;
            _address = address;
            _property = property;
            _doneByTheLink = doneByTheLink;
            _changed = changed;
            _problem = problem;
        }

        /// <summary>The first look: says what is wrong at once instead of after a while. Null: nothing.</summary>
        public string? Begin()
        {
            string? first = null;
            _beginning = true;
            try
            {
                Check(p => first = p);
            }
            catch (Exception ex)
            {
                first = ex.Message;
            }
            _beginning = false;
            return first;
        }

        public void Tick()
        {
            try
            {
                Check(_problem);
            }
            catch (Exception ex)
            {
                Complain(_problem, ex.Message);
            }
        }

        /// <summary>The link set this field: that is no change to report.</summary>
        public void Expect(string address, string json)
        {
            if (_kind == LinkWatchKind.Field && _address == address) _last = json;
        }

        public void Stop() => Detach();

        private void Check(Action<string> problem)
        {
            if (_window.GetLoadedUserControl() is not { } root) return;

            if (_kind == LinkWatchKind.Field)
            {
                ReadField(root, problem);
                return;
            }

            if (_control == null && !Attach(root, problem)) return;
            if (_polled != null) Report(_polled.GetValue(_control));
        }

        private bool Attach(Control root, Action<string> problem)
        {
            var control = LinkControls.Find(root, _address);
            if (control == null)
            {
                _missingSince ??= DateTime.UtcNow;
                if (_beginning || DateTime.UtcNow - _missingSince > GoneFor)
                    Complain(problem, $"There is no control {_address}.");
                return false;
            }

            if (_kind == LinkWatchKind.Click)
            {
                if (control is not Button button)
                {
                    Complain(problem, $"{_address} is no button.");
                    return false;
                }
                button.AddHandler(Button.ClickEvent, OnClick, RoutingStrategies.Bubble, handledEventsToo: true);
            }
            else
            {
                _watched = LinkControls.PropertyOf(control, _property!);
                _polled = _watched == null ? LinkControls.ClrPropertyOf(control, _property!) : null;
                if (_watched == null && _polled == null)
                {
                    Complain(problem, $"{_address} ({control.GetType().Name}) has no property {_property}.");
                    return false;
                }
                if (_watched != null) control.PropertyChanged += OnPropertyChanged;
            }

            control.DetachedFromVisualTree += OnDetached;
            _control = control;
            _missingSince = null;
            _complained = false;

            if (_watched != null) Report(control.GetValue(_watched));
            return true;
        }

        private void Detach()
        {
            if (_control == null) return;
            _control.DetachedFromVisualTree -= OnDetached;
            _control.PropertyChanged -= OnPropertyChanged;
            (_control as Button)?.RemoveHandler(Button.ClickEvent, OnClick);
            _control = null;
            _watched = null;
            _polled = null;
        }

        private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e) => Detach();

        private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property != _watched || _control == null) return;

            if (_doneByTheLink(_control, e.Property.Name))
            {
                _last = LinkValue.ToJson(e.NewValue);
                return;
            }
            Report(e.NewValue);
        }

        private void OnClick(object? sender, RoutedEventArgs e)
        {
            if (!ReferenceEquals(e.Source, _control) || _doneByTheLink(_control!, "Click")) return;
            _changed(null);
        }

        private void ReadField(Control root, Action<string> problem)
        {
            var type = root.GetType();
            if (type != _appType)
            {
                _appType = type;
                _member = (MemberInfo?)AppStateImage.FieldOf(type, _address)
                    ?? type.GetProperty(_address, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            }

            switch (_member)
            {
                case FieldInfo field:
                    Report(field.GetValue(field.IsStatic ? null : root));
                    break;
                case PropertyInfo property:
                    Report(property.GetValue(root));
                    break;
                default:
                    Complain(problem, $"The program has no field {_address}.");
                    break;
            }
        }

        private void Report(object? value)
        {
            var json = LinkValue.ToJson(value);
            if (json == _last) return;
            _last = json;
            _changed(json);
        }

        private void Complain(Action<string> problem, string text)
        {
            if (_complained) return;
            _complained = true;
            problem(text);
        }
    }
}
