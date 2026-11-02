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

        private ForeignApp(string program, bool dock, int size, ForeignSide side)
        {
            _program = program;
            _dock = dock;
            _size = size;
            _side = side;

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
        public static ForeignApp Attach(string program, int size = 300, ForeignSide side = ForeignSide.Right) => new(program, true, size, side);

        /// <summary>The same without docking: this app stays where it is and only reads and uses the other program.</summary>
        public static ForeignApp Watch(string program) => new(program, false, 0, ForeignSide.Right);

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

            var place = Place(VisibleFrame(target), WorkAreaOf(target), (int)Math.Round(_size * Scale(target)), _side);
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

        /// <summary>Where the panel goes: at the wanted side, at the other one if there is no room, else over the window's edge.</summary>
        internal static Rect Place(Rect window, Rect workArea, int size, ForeignSide side)
        {
            if (side == ForeignSide.Bottom)
            {
                var top = window.Bottom + size <= workArea.Bottom ? window.Bottom : window.Bottom - size;
                return new Rect(window.Left, top, window.Right, top + size);
            }

            bool fitsRight = window.Right + size <= workArea.Right, fitsLeft = window.Left - size >= workArea.Left;
            var left = side == ForeignSide.Right && fitsRight ? window.Right
                : fitsLeft ? window.Left - size
                : fitsRight ? window.Right
                : window.Right - size;
            return new Rect(left, window.Top, left + size, window.Bottom);
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

        private static string? ReadMainText(IntPtr window) => Guarded(() =>
        {
            if (MainTextElement(window) is not { } element) return null;
            var text = element.GetCurrentPattern(TextPatternId) is ITextPattern pattern
                ? pattern.DocumentRange.GetText(MaxTextLength)
                : element.GetCurrentPattern(ValuePatternId) is IValuePattern value ? value.CurrentValue : null;

            return text == null ? null : OneLineEnd(text);
        });

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

        private static IElement? Named(IntPtr window, string name)
        {
            var all = Walk(window, MaxElements);
            bool Same(string other) => string.Equals(other, name, StringComparison.OrdinalIgnoreCase);
            var match = all.FirstOrDefault(e => Same(e.Described.Name) && e.Described.IsEnabled)
                ?? all.FirstOrDefault(e => Same(e.Described.Id) && e.Described.IsEnabled)
                ?? all.FirstOrDefault(e => e.Described.IsEnabled && e.Described.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            return match?.Element;
        }

        private static bool SetValue(IElement element, string value)
        {
            if (element.GetCurrentPattern(ValuePatternId) is not IValuePattern pattern || pattern.CurrentIsReadOnly != 0)
                return false;
            pattern.SetValue(value);
            return true;
        }

        private sealed record Walked(IElement Element, ForeignElement Described, int Depth);

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

        private static void Visit(ITreeWalker walker, IElement element, int depth, List<Walked> result, int limit)
        {
            result.Add(new Walked(element, new ForeignElement(TypeName(element.CurrentControlType), element.CurrentName ?? "", element.CurrentAutomationId ?? "", element.CurrentIsEnabled != 0), depth));
            if (depth >= MaxDepth) return;
            for (var child = walker.GetFirstChildElement(element); child != null && result.Count < limit; child = walker.GetNextSiblingElement(child))
                Visit(walker, child, depth + 1, result, limit);
        }

        /// <summary>
        /// The structure of a window for the model, one control per line, indented. Names only where they
        /// name a control: what a text or a list item says is the user's content and stays here.
        /// </summary>
        internal static string DescribeStructure(IntPtr window, int maxLines)
        {
            var lines = new StringBuilder();
            foreach (var e in Walk(window, maxLines))
            {
                var name = e.Described.Type is "Text" or "ListItem" or "DataItem" or "TreeItem" or "Hyperlink" or "Image" or "Document" or "Edit"
                    ? "" : e.Described.Name;
                if (name.Length > 40) name = name[..40];
                lines.Append(' ', e.Depth).Append(e.Described.Type);
                if (name.Length > 0) lines.Append(" \"").Append(name).Append('"');
                if (e.Described.Id.Length > 0) lines.Append(" id=").Append(e.Described.Id);
                lines.Append('\n');
            }
            return lines.ToString();
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

        // ── UI Automation, declared here: its managed wrapper exists only for WPF programs ──
        // Slots that are not used still have to be there: the order is the interface. From UIAutomationClient.h.

        private const int InvokePatternId = 10000, ValuePatternId = 10002, TextPatternId = 10014;
        private const int ControlTypePropertyId = 30003, BoundingRectanglePropertyId = 30001;
        private const int EditControlType = 50004, DocumentControlType = 50030;
        private const int TreeScopeDescendants = 4;

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
            void CreatePropertyConditionEx();
            void CreateAndCondition();
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
            void FindFirst();
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
