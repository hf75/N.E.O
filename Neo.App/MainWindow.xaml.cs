#define HIDE_INTERNAL_CODE

using Neo.Agents;
using Neo.Agents.Core;
using Neo.IPC;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Newtonsoft.Json;
using NuGet.Configuration;
using Neo.App.Services;
using Neo.Shared;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using static Neo.App.NativeMethods;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using Application = System.Windows.Application;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using DataFormats = System.Windows.DataFormats;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Point = System.Windows.Point;
using UserControl = System.Windows.Controls.UserControl;

namespace Neo.App
{
    public enum ViewMode
    {
        Default,
        TxtPromptOnly,
        ContentOnly
    }

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : System.Windows.Window, IMainView
    {
        public static string AppName { get; set; } = "Neo";
        string IMainView.AppName => AppName;

        private string? titleBase;

        private bool isCycleViewLocked = true;

        private ViewMode _currentViewMode = ViewMode.Default;

        private bool _isCodeEditorActive = false;

        // Fullscreen

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_ID_FULLSCREEN = 9001;

        // Refactored
        private AppController _appController = null!; // Hinzufügen

        public AiWaitIndicator? _waitIndicator = null;

        public MainWindow()
        {
            InitializeComponent();
            this.Closing += MainWindow_Closing;
            this.Activated += MainWindow_Activated;

            this.SourceInitialized += (s, e) =>
            {
                IntPtr handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                var source = System.Windows.Interop.HwndSource.FromHwnd(handle);
                source?.AddHook(WindowProc);
            };

            // Hotkeys / Events für das Hauptfenster
            this.KeyDown += MainWindow_GlobalKeyDown;
            this.Loaded += MainWindow_Loaded;

            this.StateChanged += (s, e) =>
            {
                _appController.ChildProcessService?.NotifyParentWindowStateChanged(this.WindowState);
            };
        }

        private void OnFullScreenHotKeyPressed()
        {
            FullScreenManager.ToggleFullScreen(this);

            if (FullScreenManager.IsInFullScreen(this)) SetViewMode(ViewMode.ContentOnly);
            else SetViewMode(ViewMode.Default);

            HandleFullScreenCursor();
        }

        // In MainWindow.xaml.cs

        private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            Debug.WriteLine("--- MainWindow_Closing: Starting shutdown sequence. ---");

            // 1. Den ChildProcessService anweisen, sich selbst und alle seine Ressourcen
            //    (Prozess, Pipe, Watchdog etc.) sauber zu beenden.
            //    Die gesamte komplexe Logik von früher ist jetzt dort gekapselt.
            if (_appController.ChildProcessService != null)
            {
                await _appController.ChildProcessService.DisposeAsync();
                Debug.WriteLine("ChildProcessService disposed.");
            }

            // 2. Ressourcen des Hauptfensters aufräumen, die nichts mit dem Service zu tun haben.
            //    (z.B. Hotkeys, die an das Handle dieses Fensters gebunden sind)
            var parentHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (parentHwnd != IntPtr.Zero)
            {
                var source = HwndSource.FromHwnd(parentHwnd);
                source?.RemoveHook(WindowProc);
                UnregisterHotKey(parentHwnd, HOTKEY_ID_FULLSCREEN);
                Debug.WriteLine("WindowProc hook and hotkey unregistered.");
            }

            // 3. Andere Aufräumarbeiten, die spezifisch für das MainWindow sind.
            InactivityCursorManager.Stop();

            Debug.WriteLine("--- MainWindow_Closing: Shutdown sequence complete. ---");
        }

        private void MainWindow_Activated(object? sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _appController.ChildProcessService?.UpdatePosition();
            }), DispatcherPriority.ApplicationIdle);
        }

        private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            try
            {
                if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID_FULLSCREEN && isCycleViewLocked == false)
                {
                    OnFullScreenHotKeyPressed();
                    handled = true;
                    return IntPtr.Zero;
                }

                bool modalDialogActive = System.Windows.Interop.ComponentDispatcher.IsThreadModal;

                switch (msg)
                {
                    case NativeMethods.WM_MOUSEACTIVATE:
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _appController.ChildProcessService?.UpdatePosition(false);
                        }), DispatcherPriority.Render);
                        break;
                    case NativeMethods.WM_ACTIVATE:
                        Dispatcher.Invoke(new Action(() =>
                            {
                                _appController.ChildProcessService?.UpdatePosition(false);
                            }), DispatcherPriority.Render);
                        break;
                    case NativeMethods.WM_WINDOWPOSCHANGING:
                        Dispatcher.Invoke(new Action(() =>
                        {
                            bool useTopMostTrick = !modalDialogActive;

                            _appController.ChildProcessService?.UpdatePosition(useTopMostTrick);
                        }), DispatcherPriority.Render);
                        break;
                    case NativeMethods.WM_WINDOWPOSCHANGED:
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _appController.ChildProcessService?.UpdatePosition(false);
                        }), DispatcherPriority.Render);
                        break;
                    //case NativeMethods.WM_MOVE:
                    //    Dispatcher.BeginInvoke(new Action(() =>
                    //    {
                    //        _appController.ChildProcessService?.UpdatePosition(false);
                    //    }), DispatcherPriority.Render);
                    //    break;
                    //case NativeMethods.WM_SIZE:
                    //    Dispatcher.BeginInvoke(new Action(() =>
                    //    {
                    //        _appController.ChildProcessService?.UpdatePosition(false);
                    //    }), DispatcherPriority.Render);
                    //    break;
                    case NativeMethods.WM_NCACTIVATE:
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _appController.ChildProcessService?.UpdatePosition(false);
                        }), DispatcherPriority.Render);
                        break;
                    case NativeMethods.WM_ACTIVATEAPP:
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _appController.ChildProcessService?.UpdatePosition(false);
                        }), DispatcherPriority.Render);
                        break;
                    case NativeMethods.WM_SETFOCUS:
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _appController.ChildProcessService?.UpdatePosition(false);
                        }), DispatcherPriority.Render);
                        break;
                    case NativeMethods.WM_SHOWWINDOW:
                        Dispatcher.Invoke(new Action(() =>
                        {
                            _appController.ChildProcessService?.UpdatePosition(false);
                        }), DispatcherPriority.Render);
                        break;
                    case NativeMethods.WM_DISPLAYCHANGE:
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _appController.ChildProcessService?.UpdatePosition(false);
                        }), DispatcherPriority.Render);
                        break;
                }
            }
            catch { /* best-effort WndProc handling */ }

            handled = false;
            return IntPtr.Zero;
        }

        void EnsureAppDataFolderExists()
        {
            string? folder = Path.GetDirectoryName(_appController.DllPath);

            if (string.IsNullOrEmpty(folder) || string.IsNullOrWhiteSpace(folder))
            {
                ShowFatalErrorMessageBoxAndExit("Internal Error: 0x625312");
                return;
            }

            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            folder = Path.GetDirectoryName(_appController.NuGetPackageDirectory);

            if (string.IsNullOrEmpty(folder) || string.IsNullOrWhiteSpace(folder))
            {
                ShowFatalErrorMessageBoxAndExit("Internal Error: 0x625313");
                return;
            }

            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);
        }

        public void ShowFatalErrorMessageBoxAndExit(string errorMsg)
        {
            MessageBox.Show(
                    $"A fatal error occurred during startup:\n\n{errorMsg}\n\nThe application will now exit.",
                    "Startup Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
            );

            Application.Current.Shutdown();
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _appController = new AppController(this);

            if (_appController.AvailableAgents.Count == 0)
            {
                var setup = new Views.ApiKeySetupWindow { Owner = this };
                setup.ShowDialog();
                if (setup.KeysSaved)
                    _appController.ReloadAgents();
            }

            this.ContentRendered += OnContentRendered;

            try
            {
                // NuGet-Pakete verwalten
                FileHelper.ClearDirectory(_appController.NuGetPackageDirectory);
                // Achtung: PreloadMandatoryNugetPacks immer nach FileHelper.ClearDirectory(NuGetPackageDirectory) ausführen!
                await _appController.PreloadMandatoryNugetPacks();

                // Fenstertitel setzen
                titleBase = Title; // Aktuellen Titel als Basis speichern
                Title = titleBase + " " + $"[{_appController.AiAgent?.Name}]";

                CrossplatformSettings cps = new CrossplatformSettings()
                {
                    UseAvalonia = _appController.Settings.UseAvalonia,
                    UsePython   = _appController.Settings.UsePython,
                };

                _appController.ChildProcessService.ConfigureCrossplatformSettings(cps);

                await _appController.ChildProcessService.RestartAsync();
                _appController.ChildProcessService.HideChild();

                RegisterFullScreenHotKey();

                EnsureAppDataFolderExists();

                Activate();

                txtPrompt.Focus();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"A fatal error occurred during startup:\n\n{ex.GetType().Name}: {ex.Message}\n\nStack Trace:\n{ex.StackTrace}",
                    "Startup Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );

                Application.Current.Shutdown();
            }
        }

        private string GetUserControlBaseCode()
        {
            if (!_appController.Settings.UseAvalonia)
                return UserControlBaseCode.BaseCode;
            else
                return UserControlBaseCodeAvalonia.BaseCode;
        }

        private void RegisterFullScreenHotKey()
        {
            uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(Key.F);
            uint modifiers = (uint)(ModifierKeys.Control | ModifierKeys.Shift);

            var parentHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (!RegisterHotKey(parentHwnd, HOTKEY_ID_FULLSCREEN, modifiers, virtualKey))
            {
                MessageBox.Show("Could not register global hotkey Ctrl+Shift+F. It may already be in use by another application.");
            }
        }

        private void OnContentRendered(object? sender, EventArgs e)
        {
            _appController.ChildProcessService?.UpdatePosition(false);
        }

        public async Task SetUiBusyState(bool isBusy, string? message = null, bool showCancel = false, bool showOverlay = true)
        {
            // 1. UI-Elemente sperren/entsperren (Passiert immer)
            txtPrompt.IsEnabled = !isBusy;
            optionsHub.IsEnabled = !isBusy;
            isCycleViewLocked = isBusy;

            // 2. Overlay und Child-Process Logik
            if (isBusy)
            {
                // --- ZUSTAND: BESCHÄFTIGT ---

                if (showOverlay)
                {
                    // Child verstecken, damit es nicht über dem Overlay liegt
                    _appController.ChildProcessService?.HideChild();

                    // Indikator vorbereiten
                    if (_waitIndicator == null) _waitIndicator = CreateNewWaitUserControl(); // Safety check
                    _waitIndicator.StatusText = message ?? "";
                    _waitIndicator.Start();

                    // Cancel Button
                    btnCancel.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;

                    // Overlay anzeigen
                    dynamicContent.Content = new EmptyUserControl(); // Ggf. Platzhalter
                    GlobalOverlayContent.Content = _waitIndicator;
                    GlobalOverlay.Visibility = Visibility.Visible;
                }
                else
                {
                    // Wir sind beschäftigt, wollen aber KEIN Overlay (z.B. Hintergrundarbeit)
                    // Sicherstellen, dass Overlay weg ist, aber UI bleibt gesperrt.
                    GlobalOverlay.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                // --- ZUSTAND: BEREIT (IDLE) ---

                // Hier wird IMMER alles aufgeräumt. Man muss keine Flags setzen, um das Child wiederzuholen.

                // Overlay verstecken
                GlobalOverlay.Visibility = Visibility.Collapsed;
                GlobalOverlayContent.Content = null;
                btnCancel.Visibility = Visibility.Collapsed;

                // Indikator stoppen
                _waitIndicator?.Stop();

                // Child Process wieder einblenden
                _appController.ChildProcessService?.ShowChild();
            }
        }

        //public async Task LockUsageWithIndicator(bool lockUsage, bool showCancel = false, bool showWaitWindowIndicator = true, string reason = "")
        //{
        //    // Teil 1: UI-Steuerelemente sperren/entsperren
        //    // Diese Logik wird IMMER ausgeführt, basierend auf dem 'lockUsage'-Parameter.
        //    if (lockUsage)
        //    {
        //        txtPrompt.IsEnabled = false;
        //        optionsHub.IsEnabled = false;
        //        isCycleViewLocked = true;
        //    }
        //    else
        //    {
        //        txtPrompt.IsEnabled = true;
        //        optionsHub.IsEnabled = true;
        //        isCycleViewLocked = false;
        //    }

        //    // Teil 2: Wait-Fenster (Indikator) steuern
        //    // Dieser Teil wird nur ausgeführt, wenn 'showIndicator' true ist.
        //    if (showWaitWindowIndicator)
        //    {
        //        if (lockUsage)
        //        {
        //            _appController.ChildProcessService?.HideChild();
        //            _waitIndicator = CreateNewWaitUserControl();

        //            if (!string.IsNullOrWhiteSpace(reason))
        //                _waitIndicator.StatusText = reason;
        //            else
        //                _waitIndicator.StatusText = "";

        //            btnCancel.Visibility = showCancel == true ? Visibility.Visible : Visibility.Hidden;

        //            dynamicContent.Content = new EmptyUserControl();
        //            GlobalOverlayContent.Content = _waitIndicator;
        //            GlobalOverlay.Visibility = Visibility.Visible;

        //            _waitIndicator?.Start();
        //        }
        //        else
        //        {
        //            btnCancel.Visibility = Visibility.Hidden;
        //            dynamicContent.Content = new EmptyUserControl();
        //            _waitIndicator.Stop();

        //            GlobalOverlay.Visibility = Visibility.Collapsed;
        //            GlobalOverlayContent.Content = null;

        //            _appController.ChildProcessService?.ShowChild();
        //        }
        //    }
        //    else
        //    {
        //        btnCancel.Visibility = Visibility.Hidden;

        //        _waitIndicator?.Stop();

        //        // Wenn kein Indikator gezeigt werden soll, stellen wir sicher, dass er versteckt ist.
        //        GlobalOverlay.Visibility = Visibility.Collapsed;
        //        GlobalOverlayContent.Content = null;
        //    }
        //}

        public void EnableTxtPrompt(bool shouldEnable)
        {
#if RELEASE
    txtPrompt.IsEnabled = shouldEnable;
#else
            // Im Debug-Modus bleibt die Textbox immer aktiviert.
            txtPrompt.IsEnabled = true;
#endif
        }

        private void input_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            // Prüfen, ob das eingefügte Objekt Text ist
            if (e.DataObject.GetDataPresent(DataFormats.Text))
            {
                string text = (string)e.DataObject.GetData(DataFormats.Text);
                if (!string.IsNullOrEmpty(text))
                {
                    int caretIndex = txtPrompt.CaretIndex;
                    txtPrompt.Text = txtPrompt.Text.Insert(caretIndex, text);
                    txtPrompt.CaretIndex = caretIndex + text.Length;

                    e.CancelCommand(); // Verhindert das Standard-Einfügeverhalten
                }
            }
        }

        public void PromptToNextLine()
        {
            int caretIndex = txtPrompt.CaretIndex;
            txtPrompt.Text = txtPrompt.Text.Insert(caretIndex, "\n");
            txtPrompt.CaretIndex = caretIndex + 1;
        }

        private void input_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Return && (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                PromptToNextLine();

                e.Handled = true;
            }
        }

        private void CycleViewMode()
        {
            if (isCycleViewLocked == true)
                return;

            switch (_currentViewMode)
            {
                case ViewMode.Default:
                    SetViewMode(ViewMode.TxtPromptOnly);
                    break;
                case ViewMode.TxtPromptOnly:
                    SetViewMode(ViewMode.ContentOnly);
                    break;
                case ViewMode.ContentOnly:
                    SetViewMode(ViewMode.Default);
                    break;
            }
        }

        public bool IsContentOnlyViewActive()
        {
            return _currentViewMode == ViewMode.ContentOnly;
        }

        public void SetViewMode(ViewMode mode)
        {
            _currentViewMode = mode;

            // Zuerst: Alle Elemente in den "Standardzustand" zurücksetzen
            wvHistoryBorder.Visibility = Visibility.Visible;
            seperatorLeft.Visibility = Visibility.Visible;
            stackPanelLeft.Visibility = Visibility.Visible;
            txtPromptBorder.Visibility = Visibility.Visible;
            dynamicContentGrid.Visibility = Visibility.Visible;
            optionsHub.Visibility = Visibility.Visible;

            // Zurücksetzen der Grid-Zuordnungen (Default-Layout)
            Grid.SetRow(txtPromptBorder, 2);
            Grid.SetRowSpan(txtPromptBorder, 1);
            Grid.SetRow(optionsHub, 1);

            // Standardposition des dynamicContent (rechts, Spalte 2)
            Grid.SetRow(dynamicContentGrid, 0);
            Grid.SetRowSpan(dynamicContentGrid, 3);
            Grid.SetColumn(dynamicContentGrid, 2);
            Grid.SetColumnSpan(dynamicContentGrid, 1);

            // Setze immer den Focus auf die Textbox
            txtPrompt.Focus();

            // Je nach Modus spezifische Anpassungen:
            switch (mode)
            {
                case ViewMode.Default:
                    // Standardansicht: Keine weiteren Anpassungen notwendig
                    break;

                case ViewMode.TxtPromptOnly:
                    // Nur txtPrompt anzeigen: Alle anderen Elemente ausblenden
                    wvHistoryBorder.Visibility = Visibility.Collapsed;
                    seperatorLeft.Visibility = Visibility.Collapsed;
                    optionsHub.Visibility = Visibility.Collapsed;
                    // txtPromptBorder so anpassen, dass es den gesamten Platz in der linken Spalte einnimmt
                    Grid.SetRow(txtPromptBorder, 0);
                    Grid.SetRowSpan(txtPromptBorder, 3);
                    Grid.SetRow(optionsHub, 0);
                    break;

                case ViewMode.ContentOnly:
                    // Nur dynamicContent anzeigen: Alle anderen Elemente ausblenden
                    wvHistoryBorder.Visibility = Visibility.Collapsed;
                    seperatorLeft.Visibility = Visibility.Collapsed;
                    stackPanelLeft.Visibility = Visibility.Collapsed;
                    txtPromptBorder.Visibility = Visibility.Collapsed;
                    optionsHub.Visibility = Visibility.Collapsed;
                    // dynamicContent soll nun den gesamten Bereich einnehmen:
                    Grid.SetColumn(dynamicContentGrid, 0);
                    Grid.SetColumnSpan(dynamicContentGrid, 3);
                    // (Row und RowSpan wurden bereits auf 0 bzw. 3 gesetzt)
                    break;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                _appController.ChildProcessService?.UpdatePosition(false);
            }), DispatcherPriority.ApplicationIdle);
        }

        // ─── Code Editor Toggle ──────────────────────────────────────────

        private void BtnCodeEditor_Click(object sender, RoutedEventArgs e)
        {
            if (_isCodeEditorActive)
                HideCodeEditor();
            else
                ShowCodeEditor();
        }

        private void ShowCodeEditor()
        {
            _isCodeEditorActive = true;
            BtnCodeEditor.IsChecked = true;

            codeEditor.Text = _appController.AppState.LastCode ?? string.Empty;

            _appController.ChildProcessService?.HideChild();
            codeEditorPanel.Visibility = Visibility.Visible;

            codeEditor.Focus();
        }

        private void HideCodeEditor()
        {
            _isCodeEditorActive = false;
            BtnCodeEditor.IsChecked = false;

            codeEditorPanel.Visibility = Visibility.Collapsed;
            _appController.ChildProcessService?.ShowChild();
        }

        private async void BtnCodeEditorApply_Click(object sender, RoutedEventArgs e)
        {
            var newCode = codeEditor.Text;
            if (newCode == _appController.AppState.LastCode)
            {
                HideCodeEditor();
                return;
            }

            HideCodeEditor();
            await _appController.ApplyManualCodeEditAsync(newCode);
        }

        private void BtnCodeEditorRevert_Click(object sender, RoutedEventArgs e)
        {
            HideCodeEditor();
        }

        // ─── Frosted Snapshot ─────────────────────────────────────────────

        public void ShowFrostedSnapshot(System.Windows.Media.Imaging.BitmapSource snapshot)
        {
            frostedSnapshot.Source = snapshot;
            frostedSnapshot.Opacity = 0;
            frostedDim.Opacity = 0;
            frostedSnapshot.Visibility = Visibility.Visible;
            frostedDim.Visibility = Visibility.Visible;

            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            frostedSnapshot.BeginAnimation(OpacityProperty, fadeIn);
            frostedDim.BeginAnimation(OpacityProperty, fadeIn);
        }

        public Task HideFrostedSnapshotAsync()
        {
            if (frostedSnapshot.Visibility != Visibility.Visible)
            {
                frostedSnapshot.Source = null;
                return Task.CompletedTask;
            }

            var tcs = new System.Threading.Tasks.TaskCompletionSource();
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fadeOut.Completed += (_, _) =>
            {
                frostedSnapshot.Visibility = Visibility.Collapsed;
                frostedDim.Visibility = Visibility.Collapsed;
                frostedSnapshot.Source = null;
                tcs.TrySetResult();
            };
            frostedSnapshot.BeginAnimation(OpacityProperty, fadeOut);
            frostedDim.BeginAnimation(OpacityProperty, fadeOut);
            return tcs.Task;
        }

        public void HideFrostedSnapshot()
        {
            frostedSnapshot.BeginAnimation(OpacityProperty, null);
            frostedDim.BeginAnimation(OpacityProperty, null);
            frostedSnapshot.Visibility = Visibility.Collapsed;
            frostedDim.Visibility = Visibility.Collapsed;
            frostedSnapshot.Source = null;
        }

        // ─────────────────────────────────────────────────────────────────

        private void SwitchAI()
        {
            _appController.SwitchAI();
            Title = titleBase + " " + $"[{_appController.AiAgent?.Name}]";
        }

        private async void MainWindow_GlobalKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Wenn der Fokus im Child liegt: Parent-Shortcuts NICHT ausführen
            if (_appController.ChildProcessService.IsFocusInsideChild())
            {
                // WICHTIG: handled setzen, damit Parent-eigene CommandBindings/KeyBindings NICHT feuern.
                // Die physische Tasten-Nachricht geht trotzdem ans fokussierte HWND im Child.
                e.Handled = true;
                return;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                if (e.Key == Key.C)
                {
                    if (_isCodeEditorActive)
                        HideCodeEditor();
                    else
                        ShowCodeEditor();

                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Z)
                {
                    if (await _appController.Undo())
                        _appController.Logger.LogMessage("Undo successfull", BubbleType.Info);

                    txtPrompt.Focus();

                    e.Handled = true;

                    return;
                }
                else if (e.Key == Key.Y)
                {
                    if (_appController._undoRedo.IsRedoAmbiguous)
                    {
                        await OpenHistoryRailsAsync();
                    }
                    else
                    {
                        if (await _appController.Redo())
                            _appController.Logger.LogMessage("Redo successfull", BubbleType.Info);
                    }

                    txtPrompt.Focus();

                    e.Handled = true;

                    return;
                }
            }

            // Prüfen, ob die Strg-Taste gedrückt ist
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                // Prüfen, ob die Taste '2' (obere Reihe oder Nummernblock) gedrückt wurde
                if (e.Key == Key.D2 || e.Key == Key.NumPad2)
                {
                    CycleViewMode();
                    e.Handled = true; // Verhindert, dass das Ereignis weiter verarbeitet wird (z.B. eine '2' in ein Textfeld geschrieben wird)
                }
                else if (e.Key == Key.D1 || e.Key == Key.NumPad1)
                {
                    SwitchAI();
                    e.Handled = true;
                }
            }
            if (e.Key == Key.Return && (Keyboard.Modifiers & ModifierKeys.Shift) != ModifierKeys.Shift)
            {
                if (string.IsNullOrEmpty(txtPrompt.Text))
                    return;

                // Der neue, saubere Aufruf:
                string prompt = txtPrompt.Text;

                await _appController.ExecutePromptAsync(prompt);

                txtPrompt.Clear();
                txtPrompt.Focus();

                e.Handled = true;
            }
        }

        public async Task HardCancel()
        {
            _appController.RequestCancellation();
        }

        private async void BtnSandboxToggle_Click(object sender, RoutedEventArgs e)
        {
            // UI-Zustand sofort aktualisieren
            bool useSandbox = BtnSandboxToggle.IsChecked == true;
            BtnInternetToggle.IsEnabled = useSandbox;
            BtnFolderAccess.IsEnabled = useSandbox;

            if (!useSandbox)
            {
                BtnInternetToggle.IsChecked = false;
                BtnFolderAccess.IsChecked = false;
                // _grantedFolders.Clear(); // Diese Logik muss in den Controller
            }

            // Den Controller aufrufen
            await _appController.UpdateSandboxConfigurationAsync(
                useSandbox,
                BtnInternetToggle.IsChecked == true
            );
        }

        private async void BtnInternetToggle_Click(object sender, RoutedEventArgs e)
        {
            // Einfach den Haupt-Handler aufrufen, der den Gesamtzustand liest und anwendet
            await _appController.UpdateSandboxConfigurationAsync(
                BtnSandboxToggle.IsChecked == true,
                BtnInternetToggle.IsChecked == true
            );
        }

        private async void BtnFolderAccess_Click(object sender, RoutedEventArgs e)
        {
            // Die Logik zum Verwalten der _grantedFolders muss in den Controller.
            // Wir erstellen dafür eine neue Methode.
            bool isFolderAccessOn = BtnFolderAccess.IsChecked == true;
            _appController.SetSharedFolderAccess(isFolderAccessOn);

            // Danach die Konfiguration neu anwenden
            await _appController.UpdateSandboxConfigurationAsync(
                BtnSandboxToggle.IsChecked == true,
                BtnInternetToggle.IsChecked == true
            );
        }

        private async void BtnDesignerMode_Click(object sender, RoutedEventArgs e)
        {
            if (_appController == null) return;

            bool enable = BtnDesignerMode.IsChecked == true;
            bool ok = await _appController.SetDesignerModeAsync(enable);
            if (!ok && enable)
                BtnDesignerMode.IsChecked = false;

            if (!enable && _isCodeEditorActive)
                HideCodeEditor();
        }

        private async void HandleFullScreenCursor()
        {
            // Ermittle, ob der Cursor sichtbar sein soll.
            bool isCursorVisible = !FullScreenManager.IsInFullScreen(this);

            // Weise den Service an, die Sichtbarkeit des Cursors im Child-Prozess zu setzen.
            // Der Service kümmert sich um die gesamte IPC-Kommunikation.
            // Der ?. Operator stellt sicher, dass nichts passiert, wenn der Service noch nicht bereit ist.
            await _appController.ChildProcessService!.SetCursorVisibilityAsync(isCursorVisible);
        }

        private async void Button_Click_Clear(object sender, RoutedEventArgs e)
        {
            await PerformClear();
        }

        private async void Button_Click_HistoryRails(object sender, RoutedEventArgs e)
        {
            await OpenHistoryRailsAsync();
            txtPrompt.Focus();
        }

        private async Task OpenHistoryRailsAsync()
        {
            if (_appController == null)
                return;

            var dialog = new HistoryRailsWindow(_appController._undoRedo)
            {
                Owner = this,
            };

            var result = dialog.ShowDialog();
            if (result == true && dialog.SelectedNode != null)
                await _appController.CheckoutHistoryAsync(dialog.SelectedNode);
        }

        private async Task PerformClear()
        {
            if (_isCodeEditorActive)
                HideCodeEditor();

            await _appController.ClearSessionAsync();

            txtPrompt.Clear();
            txtPrompt.Focus();
        }

        // In MainWindow.xaml.cs
        private async void Button_Click_Import(object sender, RoutedEventArgs e)
        {
            try
            {
                // UI-Logik bleibt hier
                var dlg = new OpenFileDialog
                {
                    Title = "Choose .resx file",
                    Filter = "N.E.O. (*.resx)|*.resx",
                    CheckFileExists = true,
                    Multiselect = false
                };

                if (dlg.ShowDialog(this) == true)
                {
                    // Anwendungslogik wird an den Controller delegiert.
                    // Die LockUsageWithIndicator-Aufrufe sind weg.
                    await _appController.ImportProjectAsync(dlg.FileName);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Error while importing:\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                txtPrompt.Focus();
            }
        }

        // In MainWindow.xaml.cs
        private async void Button_Click_Export(object sender, RoutedEventArgs e)
        {
            // Die UI-Logik (Dialog anzeigen) bleibt hier.
            var dialog = new ProjectExportDialog(this, _appController.Settings);
            dialog.Owner = this;

            if(_appController.Settings.UseAvalonia == true)
            {
                dialog.crossPlattformExport.Visibility = Visibility.Visible;

                dialog.cbInstallDesktop.IsEnabled = false;
                dialog.cbInstallStartMenu.IsEnabled = false;
            }

            if (dialog.ShowDialog() == true)
            {
                CrossPlatformExport cpe = CrossPlatformExport.NONE;
                if( _appController.Settings.UseAvalonia )
                {
                    if (dialog.targetWin.IsChecked == true)
                        cpe = CrossPlatformExport.WINDOWS;
                    else if(dialog.targetLinux.IsChecked == true )
                        cpe = CrossPlatformExport.LINUX;
                    else if (dialog.targetOSX.IsChecked == true)
                        cpe = CrossPlatformExport.OSX;
                }

                ExportSettings exportSettings = new ExportSettings(cpe, _appController.Settings.UsePython);

                await _appController.ExportProjectAsync(
                    dialog.ProjectName!,
                    dialog.BaseDirectory!,
                    dialog.SelectedCreationMode!,
                    dialog.RequiresExport,
                    exportSettings,
                    dialog.ExportIcoFullPath,
                    dialog.InstallStartMenu,
                    dialog.InstallDesktop
                );
            }

            _appController.ChildProcessService.ShowChild();
        }

        public void ResetButtonMenu()
        {
            BtnSandboxHub.IsChecked = false;
            BtnSandboxToggle.IsChecked = false;
            BtnInternetToggle.IsChecked = false;
            BtnFolderAccess.IsChecked = false;
            BtnDesignerMode.IsChecked = false;
        }

        private async void Button_Click_Settings(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow();
            settingsWindow.Owner = this;
            var result = settingsWindow.ShowDialog();

            if (result == true)
            {
                SettingsModel newSettings = SettingsService.Load();

                bool requireClear = false;
                if( newSettings.UseAvalonia != _appController.Settings.UseAvalonia )
                    requireClear = true;
                if( newSettings.UsePython != _appController.Settings.UsePython )
                    requireClear = true;

                _appController.Settings = SettingsService.Load();
                _appController.ApplyModelSettings();

                if (requireClear == true)
                {
                    CrossplatformSettings settings = new CrossplatformSettings()
                    {
                        UseAvalonia = _appController.Settings.UseAvalonia,
                        UsePython = _appController.Settings.UsePython,
                    };

                    _appController.ChildProcessService.ConfigureCrossplatformSettings(settings);

                    await PerformClear();
                }
            }
        }

        public CrashDialogResult ShowCrashDialog()
        {
            var dialog = new CrashDialog(
                title: "Plugin Process Error",
                message: "The plugin process has become unresponsive or disconnected.",
                button1Text: "Restore Last Good State",
                button2Text: "Reset & Start Fresh",
                button3Text: "Close and Do Nothing"
            );
            dialog.Owner = this;
            dialog.ShowDialog();
            return dialog.Result;
        }

        public void ShowEmptyContent()
        {
            dynamicContent.Content = new EmptyUserControl();
        }

        //public void CreateNewWaitWindow()
        //{
        //    if (_waitWindow != null)
        //    {
        //        _waitWindow.Close();
        //        _waitWindow = null;
        //    }
        //    _waitWindow = new AiWaitWindow(this);

        //    _waitWindow.SourceInitialized += (s, e) =>
        //    {
        //        UpdateWaitWindowPosition();
        //    };
        //    //_waitWindow.Owner = this;
        //    //_waitWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        //}

        public AiWaitIndicator CreateNewWaitUserControl()
        {
            AiWaitIndicator waitIndicator = new AiWaitIndicator(this);

            return waitIndicator;
        }

        private async void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            await HardCancel();
        }

        // ─── IMainView Implementation ───────────────────────────────────

        string IMainView.PromptText
        {
            get => txtPrompt.Text;
            set => txtPrompt.Text = value;
        }

        void IMainView.ClearPrompt() => txtPrompt.Clear();
        void IMainView.FocusPrompt() => txtPrompt.Focus();
        void IMainView.ActivateWindow() => Activate();

        void IMainView.ShowRepairOverlay() => RepairOverlay.Visibility = Visibility.Visible;
        void IMainView.HideRepairOverlay() => RepairOverlay.Visibility = Visibility.Collapsed;

        void IMainView.ShowFrostedSnapshot(object snapshot) =>
            ShowFrostedSnapshot((System.Windows.Media.Imaging.BitmapSource)snapshot);

        void IMainView.SetWaitIndicatorStatus(string text)
        {
            if (_waitIndicator != null)
                _waitIndicator.StatusText = text;
        }

        PatchReviewDecision IMainView.ShowPatchReviewDialog(
            string patchOrCode,
            IReadOnlyList<string>? nugetPackages,
            string? explanation,
            bool isPowerShellMode,
            bool isConsoleAppMode)
        {
            var review = new PatchReviewWindow(patchOrCode, nugetPackages, explanation,
                isPowerShellMode: isPowerShellMode, isConsoleAppMode: isConsoleAppMode)
            {
                Owner = this
            };
            review.ShowDialog();
            return review.Decision;
        }

        private DesignerPropertiesWindow? _designerPropertiesWindow;

        void IMainView.ShowDesignerPropertiesWindow(DesignerSelectionMessage selection,
            EventHandler<DesignerApplyRequestedEventArgs> applyHandler)
        {
            if (_designerPropertiesWindow == null || !_designerPropertiesWindow.IsVisible)
            {
                _designerPropertiesWindow = new DesignerPropertiesWindow
                {
                    Owner = this
                };
                _designerPropertiesWindow.ApplyRequested += applyHandler;
                _designerPropertiesWindow.Closed += (_, _) => _designerPropertiesWindow = null;
                _designerPropertiesWindow.Show();
            }

            _designerPropertiesWindow.SetSelection(selection);
            _designerPropertiesWindow.RepositionNearCursor();
            _designerPropertiesWindow.Activate();
        }

        void IMainView.CloseDesignerPropertiesWindow()
        {
            if (_designerPropertiesWindow != null)
            {
                try { _designerPropertiesWindow.Close(); } catch { }
                _designerPropertiesWindow = null;
            }
        }

        bool IMainView.CheckUIThreadAccess() => Dispatcher.CheckAccess();

        void IMainView.InvokeOnUIThread(Action action) => Dispatcher.Invoke(action);

        T IMainView.InvokeOnUIThread<T>(Func<T> func) => Dispatcher.Invoke(func);

        void IMainView.InvokeOnUIThreadAsync(Action action) => _ = Dispatcher.InvokeAsync(action);
    }
}
