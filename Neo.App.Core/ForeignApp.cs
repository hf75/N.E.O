using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Neo.App
{
    /// <summary>Where a panel docks to the window of another program.</summary>
    public enum ForeignSide { Right, Left, Bottom }

    /// <summary>A control of another program, as that program reports it to the accessibility interface.</summary>
    public sealed record ForeignElement(string Type, string Name, string Id, bool IsEnabled);

    /// <summary>A dialog another program shows, a message box for one: its title, what it says, and its buttons.</summary>
    public sealed record ForeignDialog(string Title, string Text, IReadOnlyList<string> Buttons);

    /// <summary>
    /// A control is not in the other program's window: the program may have changed. When a call of another app or of
    /// Claude runs into this, N.E.O. repairs the interface with the program's new look and the call goes on. Never catch it.
    /// </summary>
    public sealed class ForeignControlMissingException : Exception
    {
        public ForeignControlMissingException(string program, string control, string? detail = null)
            : base($"{program} has no control \"{control}\"{(detail != null ? " " + detail : "")}. The program may have changed.")
        {
            Program = program;
            Control = control;
        }

        public string Program { get; }
        public string Control { get; }
    }

    /// <summary>The other program is not open.</summary>
    public sealed class ForeignProgramClosedException : Exception
    {
        public ForeignProgramClosedException(string program) : base($"{program} is not open.") => Program = program;

        public string Program { get; }
    }

    /// <summary>
    /// Another program running on this PC, whose source nobody here has. A generated app can read it, press
    /// its buttons, and dock its own window to it: the tool then looks like a part of that program. Compiled
    /// into every generated app as source, so it needs nothing from N.E.O. and also works in an exported
    /// program; N.E.O. itself uses it to show the model what a program looks like.
    /// Windows only: elsewhere a program is simply never found.
    /// </summary>
    public sealed class ForeignApp : IDisposable
    {
        // The window that runs the app must stop following N.E.O.: it reads this.
        private const string AttachedMark = "Neo.ForeignApp.Attached";

        // Every version of a generated app has its own copy of this class, and nobody tells a version that it
        // was replaced. So what the copies must agree on lies in the AppDomain: which version of the app runs
        // (the window that runs it changes this when it unloads one), who docks the window, and what the
        // window looked like before that: {style, left, top, width, height, handle}.
        private const string AppVersionMark = "Neo.ForeignApp.AppVersion";
        private const string FollowerMark = "Neo.ForeignApp.Follower";
        private const string NormalLookMark = "Neo.ForeignApp.NormalLook";

        // An interned string is the one object all copies reach. Changing the look waits for the UI thread,
        // so the UI thread must never wait for this.
        private static readonly object LookLock = string.Intern("Neo.ForeignApp.Look");

        private readonly string _program;
        private bool _dock;
        private readonly int _size;
        private readonly ForeignSide _side;
        private readonly int _length;
        private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
        private readonly object? _appVersion = AppDomain.CurrentDomain.GetData(AppVersionMark);
        private volatile bool _stopped;

        private volatile IntPtrBox? _target;
        private IntPtr _own;
        private bool _looksDocked;
        private bool _shown = true;
        private Rect _placed;
        private IntPtr _lastForeground;

        private Action<string>? _textChanged;
        private string? _lastText;

        private ForeignApp(string program, bool dock, int size, ForeignSide side, int length)
        {
            _program = program;
            _dock = dock;
            _size = size;
            _side = side;
            _length = length;

            if (!OperatingSystem.IsWindows())
                return;

            if (dock) AppDomain.CurrentDomain.SetData(FollowerMark, this);
            new Thread(FollowLoop) { IsBackground = true, Name = "ForeignApp follow" }.Start();
            new Thread(ReadLoop) { IsBackground = true, Name = "ForeignApp read" }.Start();
        }

        /// <summary>
        /// Docks this app's window to the window of another program. It follows that window, hides while
        /// the window is minimised, and goes back to being a normal window when the program ends; when
        /// the program starts again it docks again. Returns at once, whether the program is open or not.
        /// A window docks to one program: after a later Attach, this one only reads and uses its program.
        /// </summary>
        /// <param name="program">Its process name ("notepad", "EXCEL"), or a part of its window title.</param>
        /// <param name="size">The width of the panel in device-independent pixels (its height when docked at the bottom).</param>
        /// <param name="length">How long the panel is along the window's edge, in device-independent pixels; 0 makes it as long
        /// as the edge. A shorter panel sits at the lower end of a side, or at the right end of the bottom.</param>
        public static ForeignApp Attach(string program, int size = 300, ForeignSide side = ForeignSide.Right, int length = 0) => new(program, true, size, side, length);

        /// <summary>The same without docking: this app stays where it is and only reads and uses the other program.</summary>
        public static ForeignApp Watch(string program) => new(program, false, 0, ForeignSide.Right, 0);

        public string Program => _program;

        /// <summary>True while the program has a window.</summary>
        public bool IsOpen => _target != null;

        /// <summary>The title of its window; empty while it is not open.</summary>
        public string Title => _target is { } target ? WindowTitle(target.Handle) : "";

        /// <summary>The last text read from its main text area. Kept up to date while someone listens to <see cref="TextChanged"/>.</summary>
        public string Text => _lastText ?? "";

        /// <summary>The program got a window. On the thread that called Attach or Watch.</summary>
        public event Action? Opened;

        /// <summary>The program ended. On the thread that called Attach or Watch.</summary>
        public event Action? Closed;

        /// <summary>The text of its main text area changed; also runs once with the current text. On the thread that called Attach or Watch.</summary>
        public event Action<string>? TextChanged
        {
            add { _textChanged += value; _lastText = null; }
            remove { _textChanged -= value; }
        }

        /// <summary>Reads the text of its main text area now. Empty when it has none or is not open.</summary>
        public Task<string> ReadTextAsync() => Task.Run(() => _target is { } target ? ReadMainText(target.Handle) ?? "" : "");

        /// <summary>The controls in its window, in the order they appear. Empty when it is not open.</summary>
        public Task<IReadOnlyList<ForeignElement>> ReadElementsAsync() => Task.Run<IReadOnlyList<ForeignElement>>(() =>
            _target is { } target ? Walk(target.Handle, MaxElements).Select(e => e.Described).ToList() : []);

        /// <summary>
        /// The controls whose name contains this text, anywhere in its window, also deep inside a large one (a browser,
        /// an Electron app), in the order they appear. Empty when there are none or it is not open.
        /// </summary>
        public Task<IReadOnlyList<ForeignElement>> FindAsync(string name) => Task.Run<IReadOnlyList<ForeignElement>>(() =>
            _target is { } target ? Guarded(() => Containing(target.Handle, name)) ?? [] : []);

        /// <summary>Reads the text of a named field of the program. Null when there is none.</summary>
        public Task<string?> ReadValueAsync(string name) => Task.Run(() => Guarded(() =>
            _target is { } target && Named(target.Handle, name) is { } element ? TextOf(element) : null));

        /// <summary>Presses a button or menu item of the program by its name. False when there is none.</summary>
        public Task<bool> ClickAsync(string name) => Task.Run(() => Guarded(() =>
        {
            if (_target is not { } target || Named(target.Handle, name) is not { } element) return false;
            if (element.GetCurrentPattern(InvokePatternId) is not IInvokePattern invoke) return false;
            invoke.Invoke();
            return true;
        }));

        /// <summary>Replaces the text of its main text area. False when the program does not allow that.</summary>
        public Task<bool> SetTextAsync(string text) => Task.Run(() => Guarded(() =>
            _target is { } target && MainTextElement(target.Handle) is { } element && SetValue(element, text)));

        /// <summary>Sets the value of a named field of the program. False when there is none or it does not allow that.</summary>
        public Task<bool> SetValueAsync(string name, string value) => Task.Run(() => Guarded(() =>
            _target is { } target && Named(target.Handle, name) is { } element && SetValue(element, value)));

        // ── For interfaces: other apps and Claude use the program through these ──
        // A control goes by its name, its id, or an address from N.E.O.'s description of the window: "Menge:>Edit" is the
        // first field after the label "Menge:", "Edit:2" the second field. They throw instead of returning false: a control
        // that is not there throws ForeignControlMissingException, and N.E.O. repairs the interface; a program that is not
        // open throws ForeignProgramClosedException; a control that does not allow it, InvalidOperationException.

        /// <summary>Sets the text of a field, or picks the item with this text in a combo box.</summary>
        public Task SetAsync(string control, string value) => Strict(control, Need.Set, element =>
        {
            if (!SetValue(element, value) && !SelectIn(element, value))
                throw NotAllowed(control, "takes no text");
            return true;
        });

        /// <summary>Presses a button, a menu item, a link, a tab or a list item, the way a click does.</summary>
        public Task PressAsync(string control) => Strict(control, Need.Press, element =>
        {
            if (!Press(element, _program)) throw NotAllowed(control, "cannot be pressed");
            return true;
        });

        /// <summary>What a control shows: the text of a field or a label, the selected item of a list, "on" or "off" for a check box.</summary>
        public Task<string> GetAsync(string control) => Strict(control, Need.Read, element =>
            IsPassword(element) ? throw NotAllowed(control, "is a password field, and its text is not read") : Shown(element));

        /// <summary>Ticks (true) or clears (false) a check box or a toggle.</summary>
        public Task CheckAsync(string control, bool on) => Strict(control, Need.Check, element =>
        {
            if (element.GetCurrentPattern(TogglePatternId) is not ITogglePattern toggle) throw NotAllowed(control, "is no check box");
            for (int i = 0; i < 3 && (toggle.CurrentToggleState == 1) != on; i++) toggle.Toggle();
            return true;
        });

        /// <summary>Selects the item with this text in a list, a combo box, a tab or a tree.</summary>
        public Task SelectAsync(string control, string item) => Strict(control, Need.Select, element =>
        {
            if (!SelectIn(element, item)) throw NotAllowed(control, $"has no item \"{item}\"");
            return true;
        });

        /// <summary>The rows of a list or a table, each as the texts of its cells, as far as the program has them (a long list may only have the visible ones).</summary>
        public Task<IReadOnlyList<string[]>> RowsAsync(string control) => Strict<IReadOnlyList<string[]>>(control, Need.Read, Rows);

        /// <summary>Waits until the control is there and can be used: after a press that opens or changes something.</summary>
        public Task WaitForAsync(string control, int seconds = 10) => Task.Run(async () =>
        {
            var until = Environment.TickCount64 + seconds * 1000L;
            while (true)
            {
                var window = TargetOrFind();
                if (window != IntPtr.Zero) Restore(window);
                if (window != IntPtr.Zero && Guarded(() => Resolve(window, control, Need.Read) is { CurrentIsEnabled: not 0 }))
                    return;
                if (Environment.TickCount64 >= until)
                    throw window == IntPtr.Zero ? new ForeignProgramClosedException(_program) : new ForeignControlMissingException(_program, control, $"within {seconds} s");
                await Task.Delay(150).ConfigureAwait(false);
            }
        });

        /// <summary>A dialog of the program that is open now, or opens within the seconds given: a message box, a question. Null when there is none.</summary>
        public Task<ForeignDialog?> DialogAsync(int seconds = 0) => Task.Run(async () =>
        {
            var until = Environment.TickCount64 + seconds * 1000L;
            while (true)
            {
                var window = TargetOrFind();
                if (window == IntPtr.Zero) throw new ForeignProgramClosedException(_program);
                if (Guarded(() => OtherWindows(window).Select(Dialog).FirstOrDefault(d => d != null)) is { } dialog)
                    return dialog;
                if (Environment.TickCount64 >= until) return null;
                await Task.Delay(150).ConfigureAwait(false);
            }
        });

        /// <summary>Stops following the program; this app's window becomes a normal window again.</summary>
        public void Dispose() => _stopped = true;

        // ── Following the window ──────────────────────────────────────────────

        private bool Follows => ReferenceEquals(AppDomain.CurrentDomain.GetData(FollowerMark), this);

        private void FollowLoop()
        {
            var nextSearch = 0L;
            while (!_stopped && ReferenceEquals(AppDomain.CurrentDomain.GetData(AppVersionMark), _appVersion))
            {
                try
                {
                    // A later Attach docks the window now, and it has taken the window over as it is.
                    if (_dock && !Follows)
                    {
                        _dock = false;
                        _looksDocked = false;
                    }

                    var target = _target;
                    if (target != null && !IsWindow(target.Handle))
                    {
                        _target = target = null;
                        if (_looksDocked) lock (LookLock) LookNormal();
                        OnUi(() => Closed?.Invoke());
                    }

                    if (target == null && Environment.TickCount64 >= nextSearch)
                    {
                        nextSearch = Environment.TickCount64 + 500;
                        if (FindWindow(_program) is { } found)
                        {
                            _target = target = new IntPtrBox(found);
                            if (_dock && IsIconic(found)) ShowWindow(found, SW_RESTORE);
                            OnUi(() => Opened?.Invoke());
                        }
                        // The version of the app before this one docked to a program this one does not find.
                        else if (_dock && !_looksDocked && AppDomain.CurrentDomain.GetData(NormalLookMark) != null && OwnWindowFound())
                            lock (LookLock) LookNormal();
                    }

                    if (target != null && _dock)
                        Follow(target.Handle);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ForeignApp] " + ex.Message);
                }
                Thread.Sleep(30);
            }
            _stopped = true;

            // The window goes back to normal, unless a newer one has taken it over.
            try
            {
                lock (LookLock)
                {
                    if (!Follows) return;
                    if (_looksDocked) LookNormal();
                    AppDomain.CurrentDomain.SetData(FollowerMark, null);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ForeignApp] " + ex.Message);
            }
        }

        private bool OwnWindowFound()
        {
            if (_own != IntPtr.Zero) return true;

            // Docked already, and maybe hidden with its program, which Windows then does not call a main window.
            if (AppDomain.CurrentDomain.GetData(NormalLookMark) is long[] normal)
            {
                _own = (IntPtr)normal[5];
                return true;
            }

            using var self = Process.GetCurrentProcess();
            var main = self.MainWindowHandle;
            // Inside the N.E.O. window (the WPF host) there is no window of its own to dock.
            if (main == IntPtr.Zero || (GetWindowLongPtr(main, GWL_STYLE) & WS_CHILD) != 0) return false;
            _own = main;
            return true;
        }

        private void Follow(IntPtr target)
        {
            if (!OwnWindowFound()) return;

            if (!_looksDocked) lock (LookLock) LookDocked();

            if (IsIconic(target))
            {
                if (_shown) { ShowWindow(_own, SW_HIDE); _shown = false; }
                return;
            }
            if (!_shown)
            {
                ShowWindow(_own, SW_SHOWNOACTIVATE);
                _shown = true;
                Raise();
            }

            var scale = Scale(target);
            var place = Place(VisibleFrame(target), WorkAreaOf(target), (int)Math.Round(_size * scale), _side, (int)Math.Round(_length * scale));
            if (place != _placed)
            {
                SetWindowPos(_own, IntPtr.Zero, place.Left, place.Top, place.Width, place.Height, SWP_NOZORDER | SWP_NOACTIVATE);
                _placed = place;
            }

            // The program came to the front: the panel is a part of it, so it comes along.
            var foreground = GetForegroundWindow();
            if (foreground == target && _lastForeground != target) Raise();
            _lastForeground = foreground;
        }

        private void Raise()
        {
            SetWindowPos(_own, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            SetWindowPos(_own, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        private void LookDocked()
        {
            // A version of the app before this one may have docked it: then the window itself no longer says what it looked like.
            if (AppDomain.CurrentDomain.GetData(NormalLookMark) is not long[] normal)
            {
                GetWindowRect(_own, out var rect);
                normal = [GetWindowLongPtr(_own, GWL_STYLE), rect.Left, rect.Top, rect.Width, rect.Height, (long)_own];
                AppDomain.CurrentDomain.SetData(NormalLookMark, normal);
            }
            AppDomain.CurrentDomain.SetData(AttachedMark, true);
            SetWindowLongPtr(_own, GWL_STYLE, (normal[0] & ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX)) | WS_POPUP);
            SetWindowPos(_own, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            _shown = IsWindowVisible(_own);
            _looksDocked = true;
            _placed = default;
        }

        private void LookNormal()
        {
            if (AppDomain.CurrentDomain.GetData(NormalLookMark) is long[] normal)
            {
                SetWindowLongPtr(_own, GWL_STYLE, normal[0]);
                SetWindowPos(_own, IntPtr.Zero, (int)normal[1], (int)normal[2], (int)normal[3], (int)normal[4], SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                AppDomain.CurrentDomain.SetData(NormalLookMark, null);
            }
            if (!IsWindowVisible(_own)) ShowWindow(_own, SW_SHOWNOACTIVATE);
            _shown = true;
            AppDomain.CurrentDomain.SetData(AttachedMark, false);
            _looksDocked = false;
        }

        /// <summary>
        /// Where the panel goes: at the wanted side, at the other one if there is no room, else over the window's edge.
        /// A panel shorter than the edge sits at its lower end (at the bottom: its right end).
        /// </summary>
        internal static Rect Place(Rect window, Rect workArea, int size, ForeignSide side, int length = 0)
        {
            if (side == ForeignSide.Bottom)
            {
                var below = window.Bottom + size <= workArea.Bottom ? window.Bottom : window.Bottom - size;
                var start = length > 0 && length < window.Width ? window.Right - length : window.Left;
                return new Rect(start, below, window.Right, below + size);
            }

            bool fitsRight = window.Right + size <= workArea.Right, fitsLeft = window.Left - size >= workArea.Left;
            var left = side == ForeignSide.Right && fitsRight ? window.Right
                : fitsLeft ? window.Left - size
                : fitsRight ? window.Right
                : window.Right - size;
            var top = length > 0 && length < window.Height ? window.Bottom - length : window.Top;
            return new Rect(left, top, left + size, window.Bottom);
        }

        private static Rect VisibleFrame(IntPtr window)
        {
            // The window rectangle includes the invisible resize border.
            return DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS, out Rect frame, Marshal.SizeOf<Rect>()) == 0
                ? frame
                : GetWindowRect(window, out var rect) ? rect : default;
        }

        private static Rect WorkAreaOf(IntPtr window)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            return GetMonitorInfo(MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST), ref info) ? info.Work : VisibleFrame(window);
        }

        private static double Scale(IntPtr window)
        {
            var dpi = GetDpiForWindow(window);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }

        // ── Finding the program ───────────────────────────────────────────────

        internal sealed record OpenWindow(IntPtr Handle, int ProcessId, string Process, string Title);

        /// <summary>The windows a person sees right now, topmost first, without this process's own.</summary>
        internal static List<OpenWindow> OpenWindows()
        {
            var windows = new List<OpenWindow>();
            if (!OperatingSystem.IsWindows()) return windows;

            var self = Environment.ProcessId;
            EnumWindows((handle, _) =>
            {
                try
                {
                    if (IsPersonsWindow(handle) && ProcessOf(handle) is { } process && process.Id != self)
                        windows.Add(new OpenWindow(handle, process.Id, process.Name, WindowTitle(handle)));
                }
                catch (Exception ex) { Debug.WriteLine("[ForeignApp] " + ex.Message); }
                return true;
            }, IntPtr.Zero);
            return windows;
        }

        private static IntPtr? FindWindow(string program)
        {
            var all = OpenWindows();
            var found = all.FirstOrDefault(w => w.Process.Equals(program, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(w => w.Process.Contains(program, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(w => w.Title.Contains(program, StringComparison.OrdinalIgnoreCase));
            return found?.Handle;
        }

        private static bool IsPersonsWindow(IntPtr window) =>
            IsWindowVisible(window)
            && GetWindow(window, GW_OWNER) == IntPtr.Zero
            && (GetWindowLongPtr(window, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) == 0
            && GetWindowTextLength(window) > 0
            && !(DwmGetWindowAttribute(window, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0);

        private static (int Id, string Name)? ProcessOf(IntPtr window)
        {
            GetWindowThreadProcessId(window, out var id);
            using var process = Process.GetProcessById((int)id);
            if (process.ProcessName != "ApplicationFrameHost")
                return ((int)id, process.ProcessName);

            // A store app shows in a frame of the system: the program is who owns what is inside it.
            (int, string)? hosted = null;
            EnumChildWindows(window, (child, _) =>
            {
                GetWindowThreadProcessId(child, out var childId);
                if (childId == id) return true;
                using var inner = Process.GetProcessById((int)childId);
                hosted = ((int)childId, inner.ProcessName);
                return false;
            }, IntPtr.Zero);
            return hosted ?? ((int)id, process.ProcessName);
        }

        private static string WindowTitle(IntPtr window)
        {
            var text = new StringBuilder(GetWindowTextLength(window) + 1);
            GetWindowText(window, text, text.Capacity);
            return text.ToString();
        }

        // ── Reading and using it ──────────────────────────────────────────────

        private const int MaxElements = 400;
        private const int MaxDepth = 14;
        private const int MaxTextLength = 2_000_000;

        private void ReadLoop()
        {
            while (!_stopped)
            {
                Thread.Sleep(300);
                try
                {
                    if (_textChanged is not { } listeners || _target is not { } target) continue;
                    var text = ReadMainText(target.Handle);
                    if (text == null || text == _lastText) continue;
                    _lastText = text;
                    OnUi(() => listeners.Invoke(text));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ForeignApp] " + ex.Message);
                }
            }
        }

        private static string? ReadMainText(IntPtr window) => Guarded(() => MainTextElement(window) is { } element ? TextOf(element) : null);

        private static string? TextOf(IElement element)
        {
            var text = element.GetCurrentPattern(TextPatternId) is ITextPattern pattern
                ? pattern.DocumentRange.GetText(MaxTextLength)
                : element.GetCurrentPattern(ValuePatternId) is IValuePattern value ? value.CurrentValue : null;

            return text == null ? null : OneLineEnd(text);
        }

        // Notepad's text area ends its lines with a lone \r: code that splits at \n would see one line.
        internal static string OneLineEnd(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

        // The largest text area: search boxes and the like are smaller than the document they search.
        private static IElement? MainTextElement(IntPtr window)
        {
            var automation = Automation;
            var root = automation.ElementFromHandle(window);
            var condition = automation.CreateOrCondition(
                automation.CreatePropertyCondition(ControlTypePropertyId, DocumentControlType),
                automation.CreatePropertyCondition(ControlTypePropertyId, EditControlType));
            var found = root.FindAll(TreeScopeDescendants, condition);

            IElement? best = null;
            double bestArea = -1;
            for (int i = 0; i < found.Length; i++)
            {
                var element = found.GetElement(i);
                var area = element.GetCurrentPropertyValue(BoundingRectanglePropertyId) is double[] { Length: 4 } r ? r[2] * r[3] : 0;
                if (area > bestArea) { best = element; bestArea = area; }
            }
            return best;
        }

        // By its name, then by its id, then by a part of its name; enabled controls only. The whole window is searched,
        // not just its first controls: in a browser or an Electron app the ones that matter lie deep.
        private static IElement? Named(IntPtr window, string name)
        {
            var automation = Automation;
            var root = automation.ElementFromHandle(window);
            var enabled = automation.CreatePropertyCondition(IsEnabledPropertyId, true);
            IElement? First(int property, int flags) => root.FindFirst(TreeScopeDescendants,
                automation.CreateAndCondition(automation.CreatePropertyConditionEx(property, name, flags), enabled));

            return First(NamePropertyId, IgnoreCase) ?? First(AutomationIdPropertyId, IgnoreCase) ?? First(NamePropertyId, IgnoreCase | MatchSubstring);
        }

        private static List<ForeignElement> Containing(IntPtr window, string name)
        {
            var automation = Automation;
            var found = automation.ElementFromHandle(window).FindAll(TreeScopeDescendants,
                automation.CreatePropertyConditionEx(NamePropertyId, name, IgnoreCase | MatchSubstring));
            var result = new List<ForeignElement>();
            for (int i = 0; i < found.Length && result.Count < MaxElements; i++)
                result.Add(Describe(found.GetElement(i)));
            return result;
        }

        private static bool SetValue(IElement element, string value)
        {
            if (element.GetCurrentPattern(ValuePatternId) is not IValuePattern pattern || pattern.CurrentIsReadOnly != 0)
                return false;
            pattern.SetValue(value);
            return true;
        }

        private sealed record Walked(ForeignElement Described, int Depth);

        private static List<Walked> Walk(IntPtr window, int limit)
        {
            var result = new List<Walked>();
            Guarded(() =>
            {
                var automation = Automation;
                var walker = automation.ControlViewWalker;
                Visit(walker, automation.ElementFromHandle(window), 0, result, limit);
                return true;
            });
            return result;
        }

        private static ForeignElement Describe(IElement element) =>
            new(TypeName(element.CurrentControlType), element.CurrentName ?? "", element.CurrentAutomationId ?? "", element.CurrentIsEnabled != 0);

        private static void Visit(ITreeWalker walker, IElement element, int depth, List<Walked> result, int limit)
        {
            result.Add(new Walked(Describe(element), depth));
            if (depth >= MaxDepth) return;
            for (var child = walker.GetFirstChildElement(element); child != null && result.Count < limit; child = walker.GetNextSiblingElement(child))
                Visit(walker, child, depth + 1, result, limit);
        }

        // ── The calls of an interface ─────────────────────────────────────────

        // N.E.O.'s description of a window counts "Edit:3" over the same walk (Neo.Shared.ForeignWindows).
        private const int MaxWalk = 600;
        private const int MaxRows = 2000;
        private static readonly TimeSpan PressWait = TimeSpan.FromSeconds(2);

        private enum Need { Read, Set, Press, Check, Select }

        private Task<T> Strict<T>(string control, Need need, Func<IElement, T> act) => Task.Run(() =>
        {
            var window = TargetOrFind();
            if (window == IntPtr.Zero) throw new ForeignProgramClosedException(_program);
            Restore(window);
            try
            {
                // A program between two states is no changed program: one more look before it counts as one.
                var element = Resolve(window, control, need);
                if (element == null)
                {
                    Thread.Sleep(300);
                    element = Resolve(window, control, need) ?? throw new ForeignControlMissingException(_program, control);
                }
                if (need != Need.Read && element.CurrentIsEnabled == 0) throw NotAllowed(control, "is disabled right now");
                return act(element);
            }
            catch (COMException ex)
            {
                if (!IsWindow(window)) throw new ForeignProgramClosedException(_program);
                throw new InvalidOperationException($"{_program} did not answer for \"{control}\": {ex.Message}", ex);
            }
        });

        private Task Strict(string control, Need need, Func<IElement, bool> act) => Strict<bool>(control, need, act);

        // Right after Attach or Watch the follow loop may not have looked yet.
        private IntPtr TargetOrFind() => _target?.Handle ?? FindWindow(_program) ?? IntPtr.Zero;

        // A minimised program shows only its title bar, and a person cannot use it either: it comes back, without taking the keyboard.
        private static void Restore(IntPtr window)
        {
            if (!IsIconic(window)) return;
            ShowWindow(window, SW_SHOWNOACTIVATE);
            for (int i = 0; i < 20 && IsIconic(window); i++) Thread.Sleep(50);
            Thread.Sleep(250);
        }

        private InvalidOperationException NotAllowed(string control, string what) => new($"In {_program}, \"{control}\" {what}.");

        private sealed record Address(string? Label, string Type, int Index);

        /// <summary>"Menge:>Edit" (the first field after that label) or "Edit:2" (the second field); null for a name or an id.</summary>
        internal static (string? Label, string Type, int Index)? ParseAddress(string control)
        {
            var arrow = control.LastIndexOf('>');
            if (arrow > 0 && arrow < control.Length - 1 && IsTypeName(control[(arrow + 1)..].Trim()))
                return (control[..arrow].Trim(), control[(arrow + 1)..].Trim(), 0);

            var colon = control.LastIndexOf(':');
            if (colon > 0 && int.TryParse(control[(colon + 1)..], out var index) && index > 0 && IsTypeName(control[..colon]))
                return (null, control[..colon], index);
            return null;
        }

        private static bool IsTypeName(string text) => Enumerable.Range(50000, 39).Any(t => string.Equals(TypeName(t), text, StringComparison.OrdinalIgnoreCase));

        private static IElement? Resolve(IntPtr window, string control, Need need)
        {
            var automation = Automation;
            if (ParseAddress(control) is { } address)
                return AtAddress(automation, window, new Address(address.Label, address.Type, address.Index));

            // A dialog is what a person can use while it is open: the program's other windows first.
            foreach (var root in OtherWindows(window).Append(window))
            {
                if (ByName(automation, automation.ElementFromHandle(root), control, need) is { } found)
                    return found;
            }
            return null;
        }

        // Over the walk N.E.O. describes the window with: depth first, in the order the program reports its controls.
        // "X>Type" is the first control of that type after the first one called X or with the id X, or after "Type:n".
        private static IElement? AtAddress(IAutomation automation, IntPtr window, Address address)
        {
            var walker = automation.ControlViewWalker;
            var anchor = address.Label is { } label ? ParseAddress(label) : null;
            int walked = 0, seen = 0, anchorSeen = 0;
            var afterAnchor = address.Label == null;
            IElement? found = null;

            bool IsAnchor(IElement element, string type)
            {
                if (anchor is { } numbered)
                    return string.Equals(type, numbered.Type, StringComparison.OrdinalIgnoreCase) && ++anchorSeen == numbered.Index;
                return string.Equals((element.CurrentName ?? "").Trim(), address.Label, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(element.CurrentAutomationId ?? "", address.Label, StringComparison.OrdinalIgnoreCase);
            }

            void Visit(IElement element, int depth)
            {
                if (found != null || walked >= MaxWalk) return;
                walked++;
                var type = TypeName(element.CurrentControlType);
                // The system's title bar is the same in every program: its buttons do not count as the program's.
                if (type == "TitleBar") return;
                if (!afterAnchor)
                    afterAnchor = IsAnchor(element, type);
                else if (string.Equals(type, address.Type, StringComparison.OrdinalIgnoreCase) && (address.Label != null || ++seen == address.Index))
                {
                    found = element;
                    return;
                }
                if (depth >= MaxDepth) return;
                for (var child = walker.GetFirstChildElement(element); child != null && found == null && walked < MaxWalk; child = walker.GetNextSiblingElement(child))
                    Visit(child, depth + 1);
            }

            Visit(automation.ElementFromHandle(window), 0);
            return found;
        }

        // By its name, then its id, then a part of its name. Of controls with the same name, the one that does what is
        // needed: a field and its label are often called alike.
        private static IElement? ByName(IAutomation automation, IElement root, string name, Need need)
        {
            foreach (var (property, flags) in new[] { (NamePropertyId, IgnoreCase), (AutomationIdPropertyId, IgnoreCase), (NamePropertyId, IgnoreCase | MatchSubstring) })
            {
                var found = root.FindAll(TreeScopeDescendants, automation.CreatePropertyConditionEx(property, name, flags));
                IElement? first = null;
                for (int i = 0; i < found.Length; i++)
                {
                    var element = found.GetElement(i);
                    first ??= element;
                    if (Fits(element, need)) return element;
                }
                if (first != null) return first;
            }
            return null;
        }

        private static bool Fits(IElement element, Need need) => need switch
        {
            Need.Read => TypeName(element.CurrentControlType) != "Text" || element.GetCurrentPattern(ValuePatternId) != null,
            Need.Set => element.GetCurrentPattern(ValuePatternId) is IValuePattern { CurrentIsReadOnly: 0 } || IsContainer(element),
            Need.Press => element.CurrentIsEnabled != 0 && (element.GetCurrentPattern(InvokePatternId) != null || element.GetCurrentPattern(TogglePatternId) != null
                || element.GetCurrentPattern(SelectionItemPatternId) != null || element.GetCurrentPattern(ExpandCollapsePatternId) != null),
            Need.Check => element.GetCurrentPattern(TogglePatternId) != null,
            Need.Select => IsContainer(element) || element.GetCurrentPattern(SelectionItemPatternId) != null,
            _ => true,
        };

        private static bool IsContainer(IElement element) =>
            TypeName(element.CurrentControlType) is "ComboBox" or "List" or "Tab" or "Tree" or "DataGrid" or "Table" or "Menu" or "MenuBar";

        /// <summary>The program's other windows a person sees, a dialog for one, topmost first.</summary>
        private static List<IntPtr> OtherWindows(IntPtr window)
        {
            var others = new List<IntPtr>();
            // The frame of a store app belongs to the system, which frames other store apps in the same process.
            if (ClassName(window) == "ApplicationFrameWindow") return others;

            GetWindowThreadProcessId(window, out var processId);
            EnumWindows((handle, _) =>
            {
                if (handle != window && IsWindowVisible(handle)
                    && !(DwmGetWindowAttribute(handle, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0))
                {
                    GetWindowThreadProcessId(handle, out var id);
                    if (id == processId) others.Add(handle);
                }
                return true;
            }, IntPtr.Zero);
            return others;
        }

        private static string ClassName(IntPtr window)
        {
            var name = new StringBuilder(256);
            return GetClassName(window, name, name.Capacity) > 0 ? name.ToString() : "";
        }

        private static bool Press(IElement element, string program)
        {
            // A button with a window of its own is pressed the way Windows itself tells its parent: through UI Automation, a press that
            // opens a dialog holds the program, and it answers nothing more until the dialog is closed, so nobody could read or close it.
            if (element.CurrentControlType == ButtonControlType
                && element.GetCurrentPropertyValue(NativeWindowHandlePropertyId) is int handle && handle != 0
                && ClassName((IntPtr)handle).Contains("BUTTON", StringComparison.OrdinalIgnoreCase)
                && GetParent((IntPtr)handle) is var parent && parent != IntPtr.Zero)
            {
                var button = (IntPtr)handle;
                SendMessageTimeout(parent, WM_COMMAND, (IntPtr)((BN_CLICKED << 16) | (GetDlgCtrlID(button) & 0xFFFF)), button, SMTO_ABORTIFHUNG, (uint)PressWait.TotalMilliseconds, out _);
                Settle(button);
                return true;
            }

            if (element.GetCurrentPattern(InvokePatternId) is IInvokePattern invoke)
            {
                // A button that opens a dialog may not come back until the dialog is closed.
                Exception? failed = null;
                var press = new Thread(() => { try { invoke.Invoke(); } catch (Exception ex) { failed = ex; } }) { IsBackground = true, Name = "ForeignApp press" };
                press.Start();
                if (press.Join(PressWait) && failed is COMException com)
                    throw new InvalidOperationException($"{program} did not take the press: {com.Message}", com);
                Settle(element);
                return true;
            }
            if (element.GetCurrentPattern(TogglePatternId) is ITogglePattern toggle) { toggle.Toggle(); Settle(element); return true; }
            if (element.GetCurrentPattern(SelectionItemPatternId) is ISelectionItemPattern item) { item.Select(); Settle(element); return true; }
            if (element.GetCurrentPattern(ExpandCollapsePatternId) is IExpandCollapsePattern menu) { menu.Expand(); Settle(element); return true; }
            if (element.GetCurrentPattern(LegacyIAccessiblePatternId) is ILegacyIAccessiblePattern legacy) { legacy.DoDefaultAction(); Settle(element); return true; }
            return false;
        }

        // What a press started shows a moment later; a program busy with it answers once it is done.
        private static void Settle(IElement element)
        {
            Thread.Sleep(150);
            if (element.GetCurrentPropertyValue(NativeWindowHandlePropertyId) is int handle && handle != 0)
                SendMessageTimeout((IntPtr)handle, 0, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 3000, out _);
        }

        private static void Settle(IntPtr window)
        {
            Thread.Sleep(150);
            SendMessageTimeout(window, 0, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 3000, out _);
        }

        // The control is the item, or holds it: a combo box opens for it and closes again.
        private static bool SelectIn(IElement element, string item)
        {
            var automation = Automation;
            if (!IsContainer(element))
            {
                if (!string.Equals((element.CurrentName ?? "").Trim(), item.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
                return SelectItem(element);
            }

            var expand = element.GetCurrentPattern(ExpandCollapsePatternId) as IExpandCollapsePattern;
            var opened = false;
            if (expand is { CurrentExpandCollapseState: 0 })
            {
                try { expand.Expand(); opened = true; Thread.Sleep(100); } catch (COMException) { }
            }
            try
            {
                var found = element.FindFirst(TreeScopeDescendants, automation.CreatePropertyConditionEx(NamePropertyId, item, IgnoreCase))
                    ?? element.FindFirst(TreeScopeDescendants, automation.CreatePropertyConditionEx(NamePropertyId, item, IgnoreCase | MatchSubstring));
                if (found != null && SelectItem(found)) { Settle(element); return true; }
                // A combo box one can type into takes the text itself.
                return TypeName(element.CurrentControlType) == "ComboBox" && SetValue(element, item);
            }
            finally
            {
                if (opened && expand is { CurrentExpandCollapseState: 1 })
                    try { expand.Collapse(); } catch (COMException) { }
            }
        }

        private static bool SelectItem(IElement item)
        {
            if (item.GetCurrentPattern(SelectionItemPatternId) is ISelectionItemPattern selection) { selection.Select(); return true; }
            if (item.GetCurrentPattern(InvokePatternId) is IInvokePattern invoke) { invoke.Invoke(); return true; }
            if (item.GetCurrentPattern(LegacyIAccessiblePatternId) is ILegacyIAccessiblePattern legacy) { legacy.DoDefaultAction(); return true; }
            return false;
        }

        private static bool IsPassword(IElement element) => element.GetCurrentPropertyValue(IsPasswordPropertyId) is true;

        // A field says its text even when that is empty; a label is its name.
        private static string Shown(IElement element)
        {
            if (element.GetCurrentPattern(ValuePatternId) is IValuePattern value) return OneLineEnd(value.CurrentValue ?? "");
            if (element.GetCurrentPattern(TextPatternId) is ITextPattern text) return OneLineEnd(text.DocumentRange.GetText(MaxTextLength) ?? "");
            if (element.GetCurrentPattern(TogglePatternId) is ITogglePattern toggle) return toggle.CurrentToggleState switch { 1 => "on", 2 => "mixed", _ => "off" };
            if (element.GetCurrentPattern(SelectionPatternId) is ISelectionPattern selection)
            {
                var selected = selection.GetCurrentSelection();
                return string.Join(", ", Enumerable.Range(0, selected.Length).Select(i => selected.GetElement(i).CurrentName ?? ""));
            }
            return element.CurrentName ?? "";
        }

        private static IReadOnlyList<string[]> Rows(IElement list)
        {
            var walker = Automation.ControlViewWalker;
            var rows = new List<string[]>();
            for (var row = walker.GetFirstChildElement(list); row != null && rows.Count < MaxRows; row = walker.GetNextSiblingElement(row))
            {
                if (TypeName(row.CurrentControlType) is "Header" or "HeaderItem" or "ScrollBar" or "Thumb" or "Button" or "Separator") continue;
                var cells = new List<string>();
                var headers = 0;
                for (var cell = walker.GetFirstChildElement(row); cell != null; cell = walker.GetNextSiblingElement(cell))
                {
                    var type = TypeName(cell.CurrentControlType);
                    if (type is "HeaderItem" or "Header") headers++;
                    if (type is "Image" or "ScrollBar" or "Thumb") continue;
                    cells.Add(CellText(cell));
                }
                // The row of a table's column titles is no row of data.
                if (headers > 0 && headers >= cells.Count / 2) continue;
                rows.Add(cells.Count > 0 ? cells.ToArray() : [CellText(row)]);
            }
            return rows;
        }

        private static string CellText(IElement cell) =>
            cell.GetCurrentPattern(ValuePatternId) is IValuePattern value && !string.IsNullOrEmpty(value.CurrentValue) ? value.CurrentValue : cell.CurrentName ?? "";

        private static ForeignDialog? Dialog(IntPtr window)
        {
            var automation = Automation;
            var root = automation.ElementFromHandle(window);
            string Texts(int controlType)
            {
                var found = root.FindAll(TreeScopeDescendants, automation.CreatePropertyCondition(ControlTypePropertyId, controlType));
                return string.Join("\n", Enumerable.Range(0, found.Length).Select(i => (found.GetElement(i).CurrentName ?? "").Trim()).Where(t => t.Length > 0));
            }
            var buttons = Texts(ButtonControlType).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var text = Texts(TextControlType);
            if (buttons.Length == 0 && text.Length == 0) return null;
            return new ForeignDialog(WindowTitle(window), text, buttons);
        }

        private static string TypeName(int controlType) => (controlType - 50000) switch
        {
            0 => "Button", 1 => "Calendar", 2 => "CheckBox", 3 => "ComboBox", 4 => "Edit", 5 => "Hyperlink", 6 => "Image",
            7 => "ListItem", 8 => "List", 9 => "Menu", 10 => "MenuBar", 11 => "MenuItem", 12 => "ProgressBar", 13 => "RadioButton",
            14 => "ScrollBar", 15 => "Slider", 16 => "Spinner", 17 => "StatusBar", 18 => "Tab", 19 => "TabItem", 20 => "Text",
            21 => "ToolBar", 22 => "ToolTip", 23 => "Tree", 24 => "TreeItem", 25 => "Custom", 26 => "Group", 27 => "Thumb",
            28 => "DataGrid", 29 => "DataItem", 30 => "Document", 31 => "SplitButton", 32 => "Window", 33 => "Pane", 34 => "Header",
            35 => "HeaderItem", 36 => "Table", 37 => "TitleBar", 38 => "Separator",
            _ => "Control",
        };

        private void OnUi(Action action)
        {
            if (_ui != null) _ui.Post(_ => Run(action), null);
            else Run(action);

            static void Run(Action a)
            {
                try { a(); }
                catch (Exception ex) { Debug.WriteLine("[ForeignApp] " + ex.Message); }
            }
        }

        // The other program may end or refuse at any moment: that is an answer, not a failure.
        private static T? Guarded<T>(Func<T?> read)
        {
            try { return read(); }
            catch (Exception ex) when (ex is COMException or InvalidCastException or NullReferenceException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                Debug.WriteLine("[ForeignApp] " + ex.Message);
                return default;
            }
        }

        private static bool Guarded(Func<bool> act) => Guarded<bool>(act);

        private sealed record IntPtrBox(IntPtr Handle);

        [StructLayout(LayoutKind.Sequential)]
        internal readonly record struct Rect(int Left, int Top, int Right, int Bottom)
        {
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public Rect Monitor;
            public Rect Work;
            public uint Flags;
        }

        // ── Windows ───────────────────────────────────────────────────────────

        private const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        private const long WS_CHILD = 0x40000000, WS_POPUP = 0x80000000, WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_SYSMENU = 0x00080000,
            WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000, WS_EX_TOOLWINDOW = 0x00000080;
        private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
        private const int SW_HIDE = 0, SW_RESTORE = 9, SW_SHOWNOACTIVATE = 4;
        private const uint GW_OWNER = 4, MONITOR_DEFAULTTONEAREST = 2;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
        private static readonly IntPtr HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2);

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr window);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern long GetWindowLongPtr(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern long SetWindowLongPtr(IntPtr window, int index, long value);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int maxCount);
        [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
        [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr window);

        private const uint SMTO_ABORTIFHUNG = 0x2, WM_COMMAND = 0x0111;
        private const int BN_CLICKED = 0;

        // ── UI Automation, declared here: its managed wrapper exists only for WPF programs ──
        // Slots that are not used still have to be there: the order is the interface. From UIAutomationClient.h.

        private const int InvokePatternId = 10000, SelectionPatternId = 10001, ValuePatternId = 10002, ExpandCollapsePatternId = 10005,
            SelectionItemPatternId = 10010, TextPatternId = 10014, TogglePatternId = 10015, LegacyIAccessiblePatternId = 10018;
        private const int ControlTypePropertyId = 30003, BoundingRectanglePropertyId = 30001, NamePropertyId = 30005,
            IsEnabledPropertyId = 30010, AutomationIdPropertyId = 30011, IsPasswordPropertyId = 30019, NativeWindowHandlePropertyId = 30020;
        private const int ButtonControlType = 50000, EditControlType = 50004, TextControlType = 50020, DocumentControlType = 50030;
        private const int TreeScopeDescendants = 4;
        private const int IgnoreCase = 1, MatchSubstring = 2; // PropertyConditionFlags

        private static IAutomation? s_automation;
        private static IAutomation Automation => s_automation ??= (IAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e"))!)!;

        [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAutomation
        {
            void CompareElements();
            void CompareRuntimeIds();
            void GetRootElement();
            IElement ElementFromHandle(IntPtr window);
            void ElementFromPoint();
            void GetFocusedElement();
            void GetRootElementBuildCache();
            void ElementFromHandleBuildCache();
            void ElementFromPointBuildCache();
            void GetFocusedElementBuildCache();
            void CreateTreeWalker();
            ITreeWalker ControlViewWalker { get; }
            void get_ContentViewWalker();
            void get_RawViewWalker();
            void get_RawViewCondition();
            void get_ControlViewCondition();
            void get_ContentViewCondition();
            void CreateCacheRequest();
            void CreateTrueCondition();
            void CreateFalseCondition();
            ICondition CreatePropertyCondition(int property, [MarshalAs(UnmanagedType.Struct)] object value);
            ICondition CreatePropertyConditionEx(int property, [MarshalAs(UnmanagedType.Struct)] object value, int flags);
            ICondition CreateAndCondition(ICondition first, ICondition second);
            void CreateAndConditionFromArray();
            void CreateAndConditionFromNativeArray();
            ICondition CreateOrCondition(ICondition first, ICondition second);
        }

        [ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICondition { }

        [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IElement
        {
            void SetFocus();
            void GetRuntimeId();
            IElement? FindFirst(int scope, ICondition condition);
            IElementArray FindAll(int scope, ICondition condition);
            void FindFirstBuildCache();
            void FindAllBuildCache();
            void BuildUpdatedCache();
            [return: MarshalAs(UnmanagedType.Struct)] object GetCurrentPropertyValue(int property);
            void GetCurrentPropertyValueEx();
            void GetCachedPropertyValue();
            void GetCachedPropertyValueEx();
            void GetCurrentPatternAs();
            void GetCachedPatternAs();
            [return: MarshalAs(UnmanagedType.IUnknown)] object? GetCurrentPattern(int pattern);
            void GetCachedPattern();
            void GetCachedParent();
            void GetCachedChildren();
            int CurrentProcessId { get; }
            int CurrentControlType { get; }
            void get_CurrentLocalizedControlType();
            string CurrentName { [return: MarshalAs(UnmanagedType.BStr)] get; }
            void get_CurrentAcceleratorKey();
            void get_CurrentAccessKey();
            void get_CurrentHasKeyboardFocus();
            void get_CurrentIsKeyboardFocusable();
            int CurrentIsEnabled { get; }
            string CurrentAutomationId { [return: MarshalAs(UnmanagedType.BStr)] get; }
        }

        [ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IElementArray
        {
            int Length { get; }
            IElement GetElement(int index);
        }

        [ComImport, Guid("4042c624-389c-4afc-a630-9df854a541fc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITreeWalker
        {
            void GetParentElement();
            IElement? GetFirstChildElement(IElement element);
            void GetLastChildElement();
            IElement? GetNextSiblingElement(IElement element);
        }

        [ComImport, Guid("a94cd8b1-0844-4cd6-9d2d-640537ab39e9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IValuePattern
        {
            void SetValue([MarshalAs(UnmanagedType.BStr)] string value);
            string CurrentValue { [return: MarshalAs(UnmanagedType.BStr)] get; }
            int CurrentIsReadOnly { get; }
        }

        [ComImport, Guid("fb377fbe-8ea6-46d5-9c73-6499642d3059"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IInvokePattern
        {
            void Invoke();
        }

        [ComImport, Guid("5ed5202e-b2ac-47a6-b638-4b0bf140d78e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISelectionPattern
        {
            IElementArray GetCurrentSelection();
        }

        [ComImport, Guid("a8efa66a-0fda-421a-9194-38021f3578ea"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISelectionItemPattern
        {
            void Select();
        }

        [ComImport, Guid("94cf8058-9b8d-4ab9-8bfd-4cd0a33c8c70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITogglePattern
        {
            void Toggle();
            int CurrentToggleState { get; } // 0 off, 1 on, 2 neither
        }

        [ComImport, Guid("619be086-1f4e-4ee4-bafa-210128738730"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IExpandCollapsePattern
        {
            void Expand();
            void Collapse();
            int CurrentExpandCollapseState { get; } // 0 collapsed, 1 expanded
        }

        [ComImport, Guid("828055ad-355b-4435-86d5-3b51c14a9b1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ILegacyIAccessiblePattern
        {
            void Select(int flags);
            void DoDefaultAction();
        }

        [ComImport, Guid("32eba289-3583-42c9-9c59-3b6d9a1e9b6a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITextPattern
        {
            void RangeFromPoint();
            void RangeFromChild();
            void GetSelection();
            void GetVisibleRanges();
            ITextRange DocumentRange { get; }
        }

        [ComImport, Guid("a543cc6a-f4ae-494b-8239-c814481187a8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITextRange
        {
            void Clone();
            void Compare();
            void CompareEndpoints();
            void ExpandToEnclosingUnit();
            void FindAttribute();
            void FindText();
            void GetAttributeValue();
            void GetBoundingRectangles();
            void GetEnclosingElement();
            [return: MarshalAs(UnmanagedType.BStr)] string GetText(int maxLength);
        }
    }
}
