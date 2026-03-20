using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

using Neo.IPC;

namespace Neo.App
{
    public enum ViewMode
    {
        Default,
        TxtPromptOnly,
        ContentOnly
    }

    public partial class MainWindow : Window, IMainView
    {
        public static string AppName { get; set; } = "Neo";
        string IMainView.AppName => AppName;

        private string? titleBase;
        private bool isCycleViewLocked = true;
        private ViewMode _currentViewMode = ViewMode.Default;
        private bool _isCodeEditorActive = false;

        // Fullscreen state
        private bool _isFullScreen = false;
        private WindowState _preFullScreenState = WindowState.Normal;
        private SystemDecorations _preFullScreenDecorations = SystemDecorations.Full;

        private AppController _appController = null!;
        // Wait indicator is now shown in the Live Preview child window, not here
        private DesignerPropertiesWindow? _designerPropertiesWindow;

        public MainWindow()
        {
            InitializeComponent();

            // Position both windows centered together on screen:
            // [MainWindow 600px] [8px gap] [ChildWindow 800px] = 1408px total
            CenterWithChild(mainWidth: 600, childWidth: 800, gap: 8);

            this.Closing += MainWindow_Closing;
            this.Activated += MainWindow_Activated;
            this.AddHandler(KeyDownEvent, MainWindow_GlobalKeyDown, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);
            this.Loaded += MainWindow_Loaded;

            // Notify child process when window state changes (minimize/maximize/restore)
            this.PropertyChanged += (s, e) =>
            {
                if (e.Property == WindowStateProperty)
                {
                    _appController?.ChildProcessService?.NotifyParentWindowStateChanged(WindowState switch
                    {
                        WindowState.Minimized => HostWindowState.Minimized,
                        WindowState.Maximized => HostWindowState.Maximized,
                        _ => HostWindowState.Normal
                    });
                }
            };

            // Magnetic docking: send parent bounds to child on move/resize
            this.PositionChanged += (s, e) => _appController?.ChildProcessService?.UpdatePosition();
            this.SizeChanged += (s, e) => _appController?.ChildProcessService?.UpdatePosition();

        }

        private void CenterWithChild(double mainWidth, double childWidth, double gap)
        {
            var screen = Screens.Primary;
            if (screen == null) return;

            double totalWidth = mainWidth + gap + childWidth;
            double screenW = screen.WorkingArea.Width / (screen.Scaling > 0 ? screen.Scaling : 1);
            double screenH = screen.WorkingArea.Height / (screen.Scaling > 0 ? screen.Scaling : 1);

            double startX = (screenW - totalWidth) / 2;
            double startY = (screenH - Height) / 2;

            if (startX < 0) startX = 0;
            if (startY < 0) startY = 0;

            Position = new PixelPoint((int)(startX * (screen.Scaling > 0 ? screen.Scaling : 1)),
                                      (int)(startY * (screen.Scaling > 0 ? screen.Scaling : 1)));
        }

        private async void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
        {
            Debug.WriteLine("--- MainWindow_Closing: Starting shutdown sequence. ---");

            if (_appController?.ChildProcessService != null)
            {
                await _appController.ChildProcessService.DisposeAsync();
                Debug.WriteLine("ChildProcessService disposed.");
            }

            Debug.WriteLine("--- MainWindow_Closing: Shutdown sequence complete. ---");
        }

        private void MainWindow_Activated(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _appController?.ChildProcessService?.UpdatePosition();
            }, DispatcherPriority.Background);
        }

        private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            // Force UseAvalonia BEFORE AppController constructor, because
            // the constructor calls RecreatePromptToDllSession which reads
            // Settings.UseAvalonia to select the correct base code template.
            var preSettings = SettingsService.Load();
            preSettings.UseAvalonia = true;
            SettingsService.Save(preSettings);

            _appController = new AppController(this);

            if (_appController.AvailableAgents.Count == 0)
            {
                var setup = new Neo.App.Views.ApiKeySetupWindow();
                await setup.ShowDialog<object?>(this);
                if (setup.KeysSaved)
                    _appController.ReloadAgents();
            }

            try
            {
                FileHelper.ClearDirectory(_appController.NuGetPackageDirectory);

                // Register Avalonia DLLs from the host's bin directory instead of
                // downloading them from NuGet. This is instant, works offline, and
                // guarantees version consistency with the host.
                RegisterHostAvaloniaDlls();

                await _appController.PreloadMandatoryNugetPacks();

                titleBase = Title;
                Title = titleBase + " " + $"[{_appController.AiAgent?.Name}]";

                CrossplatformSettings cps = new CrossplatformSettings()
                {
                    UseAvalonia = true,
                    UsePython = _appController.Settings.UsePython,
                };

                _appController.ChildProcessService.ConfigureCrossplatformSettings(cps);

                await _appController.ChildProcessService.RestartAsync();
                // Don't call HideChild here — the child's WaitOverlay is already
                // visible by default (XAML). HideChild would start the timer.

                EnsureAppDataFolderExists();

                isCycleViewLocked = false;
                Activate();
                txtPrompt.Focus();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainWindow_Loaded] Fatal error: {ex}");
            }
        }

        private void RegisterHostAvaloniaDlls()
        {
            // Use Avalonia DLLs from the host's own bin directory instead of
            // downloading them from NuGet. Faster, offline-capable, version-consistent.
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var avaloniaDlls = new List<string>();

            foreach (var pattern in new[] { "Avalonia*.dll", "HarfBuzzSharp*.dll",
                "SkiaSharp*.dll", "MicroCom.Runtime.dll", "Tmds.DBus.Protocol.dll" })
            {
                try
                {
                    foreach (var dll in Directory.GetFiles(baseDir, pattern))
                    {
                        avaloniaDlls.Add(dll);
                    }
                }
                catch { }
            }

            if (avaloniaDlls.Count > 0)
            {
                // Add to AppState so they're available for compilation and export
                var existing = new HashSet<string>(
                    _appController.AppState.NuGetDlls ?? new List<string>(),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var dll in avaloniaDlls)
                {
                    if (existing.Add(dll))
                        _appController.AppState.NuGetDlls.Add(dll);
                }

                Debug.WriteLine($"[Startup] Registered {avaloniaDlls.Count} Avalonia DLLs from host bin directory");
            }
        }

        private void EnsureAppDataFolderExists()
        {
            string? folder = Path.GetDirectoryName(_appController.DllPath);
            if (!string.IsNullOrWhiteSpace(folder) && !Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            folder = Path.GetDirectoryName(_appController.NuGetPackageDirectory);
            if (!string.IsNullOrWhiteSpace(folder) && !Directory.Exists(folder))
                Directory.CreateDirectory(folder);
        }

        // ─── UI Busy State ──────────────────────────────────────────────

        public async Task SetUiBusyState(bool isBusy, string? message = null, bool showCancel = false, bool showOverlay = true)
        {
            txtPrompt.IsEnabled = !isBusy;
            optionsHub.IsEnabled = !isBusy;
            isCycleViewLocked = isBusy;
            btnCancelContainer.IsVisible = isBusy && showCancel;

            // Yield to let Avalonia render before heavy sync operations
            await Task.Delay(1);
        }

        // ─── Prompt Handling ────────────────────────────────────────────

        public void PromptToNextLine()
        {
            var caretIndex = txtPrompt.CaretIndex;
            txtPrompt.Text = (txtPrompt.Text ?? "").Insert(caretIndex, "\n");
            txtPrompt.CaretIndex = caretIndex + 1;
        }

        private void input_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return)
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    PromptToNextLine();
                }
                // Always handle Return to prevent TextBox from inserting newlines
                e.Handled = true;
            }
        }

        // ─── View Mode ─────────────────────────────────────────────────

        private void CycleViewMode()
        {
            if (isCycleViewLocked) return;

            switch (_currentViewMode)
            {
                case ViewMode.Default:
                    SetViewMode(ViewMode.TxtPromptOnly);
                    break;
                case ViewMode.TxtPromptOnly:
                    SetViewMode(ViewMode.Default);
                    break;
            }
        }

        public void SetViewMode(ViewMode mode)
        {
            _currentViewMode = mode;

            // Reset all to visible
            wvHistoryBorder.IsVisible = true;
            seperatorLeft.IsVisible = true;
            txtPromptBorder.IsVisible = true;
            optionsHub.IsVisible = true;

            Grid.SetRow(txtPromptBorder, 2);
            Grid.SetRowSpan(txtPromptBorder, 1);

            txtPrompt.Focus();

            switch (mode)
            {
                case ViewMode.Default:
                    break;

                case ViewMode.TxtPromptOnly:
                    wvHistoryBorder.IsVisible = false;
                    seperatorLeft.IsVisible = false;
                    optionsHub.IsVisible = false;
                    Grid.SetRow(txtPromptBorder, 0);
                    Grid.SetRowSpan(txtPromptBorder, 3);
                    break;
            }
        }

        // ─── Code Editor ────────────────────────────────────────────────

        private async void BtnCodeEditor_Click(object? sender, RoutedEventArgs e)
        {
            if (_appController == null) return;

            _isCodeEditorActive = true;
            BtnCodeEditor.IsChecked = true;

            // Use current code from the virtual project (most up-to-date), fallback to AppState
            string currentCode = _appController.GetCurrentCode()
                ?? _appController.AppState.LastCode
                ?? string.Empty;
            Debug.WriteLine($"[CodeEditor] GetCurrentCode={_appController.GetCurrentCode()?.Length ?? -1}, LastCode={_appController.AppState.LastCode?.Length ?? -1}, final={currentCode.Length}");
            var editorWindow = new Views.CodeEditorWindow(currentCode);
            await editorWindow.ShowDialog<object?>(this);

            _isCodeEditorActive = false;
            BtnCodeEditor.IsChecked = false;

            if (editorWindow.Applied && editorWindow.EditedCode != _appController.AppState.LastCode)
            {
                await _appController.ApplyManualCodeEditAsync(editorWindow.EditedCode ?? "");
            }
        }

        private void ShowCodeEditor() { BtnCodeEditor_Click(null, null!); }
        private void HideCodeEditor() { _isCodeEditorActive = false; BtnCodeEditor.IsChecked = false; }

        // ─── Frosted Snapshot (no-op: child runs as separate window) ────

        public Task HideFrostedSnapshotAsync() => Task.CompletedTask;
        public void HideFrostedSnapshot() { }

        // ─── Global Key Handling ────────────────────────────────────────

        private async void MainWindow_GlobalKeyDown(object? sender, KeyEventArgs e)
        {
            if (_appController == null) return;

            if (_appController.ChildProcessService != null && _appController.ChildProcessService.IsFocusInsideChild())
            {
                e.Handled = true;
                return;
            }

            // F11: Toggle fullscreen
            if (e.Key == Key.F11 && !isCycleViewLocked)
            {
                ToggleFullScreen();
                e.Handled = true;
                return;
            }

            // Escape: Cancel current operation
            if (e.Key == Key.Escape)
            {
                if (_isFullScreen)
                {
                    ToggleFullScreen();
                    e.Handled = true;
                    return;
                }

                if (_isCodeEditorActive)
                {
                    HideCodeEditor();
                    e.Handled = true;
                    return;
                }

                await HardCancel();
                e.Handled = true;
                return;
            }

            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
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

                if (e.Key == Key.F)
                {
                    // Toggle child window fullscreen
                    if (_appController?.ChildProcessService?.HasLoadedControl == true)
                        ToggleChildFullScreen();
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

                if (e.Key == Key.Y)
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

            if (e.KeyModifiers == KeyModifiers.Control)
            {
                if (e.Key == Key.D2)
                {
                    CycleViewMode();
                    e.Handled = true;
                }
                else if (e.Key == Key.D1)
                {
                    SwitchAI();
                    e.Handled = true;
                }
            }

            if (e.Key == Key.Return && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                if (string.IsNullOrEmpty(txtPrompt.Text))
                    return;

                string prompt = txtPrompt.Text;
                await _appController.ExecutePromptAsync(prompt);
                txtPrompt.Clear();
                txtPrompt.Focus();
                e.Handled = true;
            }
        }

        // ─── Fullscreen ─────────────────────────────────────────────────

        private void ToggleFullScreen()
        {
            if (_isFullScreen)
            {
                // Restore previous state
                SystemDecorations = _preFullScreenDecorations;
                WindowState = _preFullScreenState;
                _isFullScreen = false;
                SetViewMode(ViewMode.Default);
            }
            else
            {
                // Save current state and go fullscreen
                _preFullScreenState = WindowState;
                _preFullScreenDecorations = SystemDecorations;
                SystemDecorations = SystemDecorations.None;
                WindowState = WindowState.FullScreen;
                _isFullScreen = true;
                SetViewMode(ViewMode.ContentOnly);
            }
        }

        private void ToggleChildFullScreen()
        {
            if (_appController?.ChildProcessService is AvaloniaChildProcessService svc)
                svc.SendToggleFullScreenAsync();
        }

        private void SwitchAI()
        {
            _appController.SwitchAI();
            Title = titleBase + " " + $"[{_appController.AiAgent?.Name}]";
        }

        // ─── Toolbar Button Handlers ────────────────────────────────────

        private async void Button_Click_Clear(object? sender, RoutedEventArgs e)
        {
            await PerformClear();
        }

        private async void Button_Click_HistoryRails(object? sender, RoutedEventArgs e)
        {
            await OpenHistoryRailsAsync();
            txtPrompt.Focus();
        }

        private async Task OpenHistoryRailsAsync()
        {
            if (_appController == null) return;

            var dialog = new HistoryRailsWindow(_appController._undoRedo);
            var result = await dialog.ShowDialog<bool?>(this);
            if (result == true && dialog.SelectedNode != null)
                await _appController.CheckoutHistoryAsync(dialog.SelectedNode);
        }

        private async Task PerformClear()
        {
            if (_isCodeEditorActive)
                HideCodeEditor();

            try
            {
                Debug.WriteLine("[PerformClear] Starting ClearSessionAsync...");
                await _appController.ClearSessionAsync();
                Debug.WriteLine("[PerformClear] ClearSessionAsync completed.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PerformClear] EXCEPTION: {ex}");
                // Ensure UI is unblocked even if Clear fails
                try { await SetUiBusyState(false); } catch { }
            }

            txtPrompt.Clear();
            txtPrompt.Focus();
        }

        private async void Button_Click_Import(object? sender, RoutedEventArgs e)
        {
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Choose .resx file",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("N.E.O.") { Patterns = new[] { "*.resx" } }
                    }
                });

                if (files.Count > 0)
                {
                    var path = files[0].TryGetLocalPath();
                    if (path != null)
                        await _appController.ImportProjectAsync(path);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Import] Error: {ex.Message}");
            }
            finally
            {
                txtPrompt.Focus();
            }
        }

        private async void Button_Click_Export(object? sender, RoutedEventArgs e)
        {
            var dialog = new ProjectExportDialog(_appController.Settings);
            var result = await dialog.ShowDialog<bool?>(this);

            if (result == true)
            {
                CrossPlatformExport cpe = CrossPlatformExport.NONE;
                if (_appController.Settings.UseAvalonia)
                {
                    cpe = dialog.SelectedExportTarget;
                }

                ExportSettings exportSettings = new ExportSettings(cpe, _appController.Settings.UsePython, UseAvalonia: true);

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

            _appController.ChildProcessService?.ShowChild();
        }

        private async void BtnSandboxToggle_Click(object? sender, RoutedEventArgs e)
        {
            bool useSandbox = BtnSandboxToggle.IsChecked == true;
            BtnInternetToggle.IsEnabled = useSandbox;
            BtnFolderAccess.IsEnabled = useSandbox;

            if (!useSandbox)
            {
                BtnInternetToggle.IsChecked = false;
                BtnFolderAccess.IsChecked = false;
            }

            await _appController.UpdateSandboxConfigurationAsync(
                useSandbox,
                BtnInternetToggle.IsChecked == true
            );
        }

        private async void BtnInternetToggle_Click(object? sender, RoutedEventArgs e)
        {
            await _appController.UpdateSandboxConfigurationAsync(
                BtnSandboxToggle.IsChecked == true,
                BtnInternetToggle.IsChecked == true
            );
        }

        private async void BtnFolderAccess_Click(object? sender, RoutedEventArgs e)
        {
            bool isFolderAccessOn = BtnFolderAccess.IsChecked == true;
            _appController.SetSharedFolderAccess(isFolderAccessOn);

            await _appController.UpdateSandboxConfigurationAsync(
                BtnSandboxToggle.IsChecked == true,
                BtnInternetToggle.IsChecked == true
            );
        }

        private async void BtnDesignerMode_Click(object? sender, RoutedEventArgs e)
        {
            if (_appController == null) return;

            bool enable = BtnDesignerMode.IsChecked == true;
            bool ok = await _appController.SetDesignerModeAsync(enable);
            if (!ok && enable)
                BtnDesignerMode.IsChecked = false;

            if (!enable && _isCodeEditorActive)
                HideCodeEditor();
        }

        private async void Button_Click_Settings(object? sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow();
            var result = await settingsWindow.ShowDialog<bool?>(this);

            if (result == true)
            {
                SettingsModel newSettings = SettingsService.Load();

                bool requireClear = false;
                if (newSettings.UseAvalonia != _appController.Settings.UseAvalonia)
                    requireClear = true;
                if (newSettings.UsePython != _appController.Settings.UsePython)
                    requireClear = true;

                _appController.Settings = SettingsService.Load();
                _appController.ApplyModelSettings();

                if (requireClear)
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

        private async void btnCancel_Click(object? sender, RoutedEventArgs e)
        {
            await HardCancel();
        }

        public async Task HardCancel()
        {
            _appController.RequestCancellation();
        }

        public AiWaitIndicator CreateNewWaitUserControl() => new AiWaitIndicator();

        public void ResetButtonMenu()
        {
            BtnSandboxHub.IsChecked = false;
            BtnSandboxToggle.IsChecked = false;
            BtnInternetToggle.IsChecked = false;
            BtnFolderAccess.IsChecked = false;
            BtnDesignerMode.IsChecked = false;
        }

        public async Task<CrashDialogResult> ShowCrashDialogAsync()
        {
            var dialog = new CrashDialog(
                title: "Plugin Process Error",
                message: "The plugin process has become unresponsive or disconnected.",
                button1Text: "Restore Last Good State",
                button2Text: "Reset & Start Fresh",
                button3Text: "Close and Do Nothing"
            );
            await dialog.ShowDialog<CrashDialogResult?>(this);
            return dialog.Result;
        }

        public void ShowEmptyContent() { /* No right panel in Avalonia — child is a separate window */ }

        // ─── IMainView Implementation ───────────────────────────────────

        string IMainView.PromptText
        {
            get => txtPrompt.Text ?? "";
            set => txtPrompt.Text = value;
        }

        void IMainView.ClearPrompt() => txtPrompt.Clear();
        void IMainView.FocusPrompt() => txtPrompt.Focus();
        void IMainView.ActivateWindow() => Activate();

        void IMainView.ShowRepairOverlay() => RepairOverlay.IsVisible = true;
        void IMainView.HideRepairOverlay() => RepairOverlay.IsVisible = false;

        void IMainView.ShowFrostedSnapshot(object snapshot) { /* No-op: child is a separate window */ }

        void IMainView.SetWaitIndicatorStatus(string text) { /* Status shown in Live Preview child window */ }

        async Task<PatchReviewDecision> IMainView.ShowPatchReviewDialogAsync(
            string patchOrCode,
            IReadOnlyList<string>? nugetPackages,
            string? explanation,
            bool isPowerShellMode,
            bool isConsoleAppMode)
        {
            var review = new PatchReviewWindow(patchOrCode, nugetPackages, explanation,
                isPowerShellMode: isPowerShellMode, isConsoleAppMode: isConsoleAppMode);
            await review.ShowDialog<bool?>(this);
            return review.Decision;
        }

        void IMainView.ShowDesignerPropertiesWindow(DesignerSelectionMessage selection,
            EventHandler<DesignerApplyRequestedEventArgs> applyHandler)
        {
            if (_designerPropertiesWindow == null || !_designerPropertiesWindow.IsVisible)
            {
                _designerPropertiesWindow = new DesignerPropertiesWindow();
                _designerPropertiesWindow.ApplyRequested += applyHandler;
                _designerPropertiesWindow.Closed += (_, _) => _designerPropertiesWindow = null;
                _designerPropertiesWindow.Show();
                _designerPropertiesWindow.Topmost = true;
            }

            _designerPropertiesWindow.SetSelection(selection);
            // Toggle Topmost off/on to force window manager to bring it to front
            _designerPropertiesWindow.Topmost = false;
            _designerPropertiesWindow.Topmost = true;
            _designerPropertiesWindow.Activate();
            _designerPropertiesWindow.Focus();
        }

        void IMainView.CloseDesignerPropertiesWindow()
        {
            if (_designerPropertiesWindow != null)
            {
                try { _designerPropertiesWindow.Close(); } catch { }
                _designerPropertiesWindow = null;
            }
        }

        IChildProcessService IMainView.CreateChildProcessService() =>
            new AvaloniaChildProcessService(this);

        IAppLogger IMainView.CreateLogger(ApplicationState appState) =>
            new AvaloniaAppLogger(appState, historyView);

        bool IMainView.CheckUIThreadAccess() => Dispatcher.UIThread.CheckAccess();

        void IMainView.InvokeOnUIThread(Action action) => Dispatcher.UIThread.Invoke(action);

        T IMainView.InvokeOnUIThread<T>(Func<T> func) => Dispatcher.UIThread.Invoke(func);

        void IMainView.InvokeOnUIThreadAsync(Action action) => Dispatcher.UIThread.Post(action);
    }
}
