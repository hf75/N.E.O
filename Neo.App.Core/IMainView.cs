using Neo.IPC;

namespace Neo.App
{
    /// <summary>What the person chose for an app link.</summary>
    /// <param name="Blocks">The numbers of the chosen blocks of the proposal.</param>
    /// <param name="Titles">What they do, in the words of the proposal.</param>
    public sealed record LinkChoice(IReadOnlyList<int> Blocks, IReadOnlyList<string> Titles);

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

        /// <summary>A question only the person can answer; true for the first answer.</summary>
        Task<bool> AskAsync(string title, string question, string yes, string no) => Task.FromResult(false);

        /// <summary>The person chooses what an app link connects. Null: no link.</summary>
        /// <param name="propose">Asks the model: what the person wished for in their own words (null: nothing), and what was suggested before.</param>
        Task<LinkChoice?> ChooseLinkAsync(string title, Func<string?, Neo.AssemblyForge.LinkProposal?, CancellationToken, Task<Neo.AssemblyForge.LinkProposal>> propose) => Task.FromResult<LinkChoice?>(null);

        Task<PatchReviewDecision> ShowPatchReviewDialogAsync(
            string patchOrCode,
            IReadOnlyList<string>? nugetPackages,
            string? explanation,
            bool isPowerShellMode = false,
            bool isConsoleAppMode = false,
            string? webSearchReport = null);

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
