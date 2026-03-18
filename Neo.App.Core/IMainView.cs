using Neo.IPC;

namespace Neo.App
{
    /// <summary>
    /// Abstraction layer between AppController and the host window (WPF/Avalonia).
    /// Implemented by MainWindow in each UI framework.
    /// </summary>
    public interface IMainView
    {
        // ─── App Identity ────────────────────────────────────────────────
        string AppName { get; }

        // ─── UI Busy State ───────────────────────────────────────────────
        Task SetUiBusyState(bool isBusy, string? message = null, bool showCancel = false, bool showOverlay = true);

        // ─── Prompt Management ───────────────────────────────────────────
        string PromptText { get; set; }
        void PromptToNextLine();
        void ClearPrompt();
        void FocusPrompt();

        // ─── Frosted Snapshot Overlay ────────────────────────────────────
        /// <summary>Shows a frosted screenshot overlay. The snapshot type is platform-specific (BitmapSource/Bitmap).</summary>
        void ShowFrostedSnapshot(object snapshot);
        Task HideFrostedSnapshotAsync();
        void HideFrostedSnapshot();

        // ─── Content Area ────────────────────────────────────────────────
        void ShowEmptyContent();

        // ─── Window Operations ───────────────────────────────────────────
        void ActivateWindow();
        void ResetButtonMenu();

        // ─── Repair Overlay ──────────────────────────────────────────────
        void ShowRepairOverlay();
        void HideRepairOverlay();

        // ─── Wait Indicator ──────────────────────────────────────────────
        void SetWaitIndicatorStatus(string text);

        // ─── Dialogs ─────────────────────────────────────────────────────
        Task<CrashDialogResult> ShowCrashDialogAsync();

        Task<PatchReviewDecision> ShowPatchReviewDialogAsync(
            string patchOrCode,
            IReadOnlyList<string>? nugetPackages,
            string? explanation,
            bool isPowerShellMode = false,
            bool isConsoleAppMode = false);

        // ─── Designer ────────────────────────────────────────────────────
        void ShowDesignerPropertiesWindow(DesignerSelectionMessage selection,
            EventHandler<DesignerApplyRequestedEventArgs> applyHandler);
        void CloseDesignerPropertiesWindow();

        // ─── Factory Methods ──────────────────────────────────────────────
        IChildProcessService CreateChildProcessService();
        IAppLogger CreateLogger(ApplicationState appState);

        // ─── Threading ───────────────────────────────────────────────────
        bool CheckUIThreadAccess();
        void InvokeOnUIThread(Action action);
        void InvokeOnUIThreadAsync(Action action);
        T InvokeOnUIThread<T>(Func<T> func);
    }
}
