using Neo.IPC;

namespace Neo.App
{
    /// <summary>
    /// Cross-platform interface for managing the child process that hosts the generated UI.
    /// </summary>
    public interface IChildProcessService : IAsyncDisposable
    {
        Task RestartAsync();
        void UpdatePosition(bool useTopMostTrick = false);
        Task<bool> DisplayControlAsync(string mainDllPath, IEnumerable<string> nugetDlls, IEnumerable<string> additionalDlls);
        bool IsFocusInsideChild();
        void ConfigureSandbox(bool useSandbox, SandboxSettings settings);
        void ConfigureCrossplatformSettings(CrossplatformSettings settings);
        void NotifyParentWindowStateChanged(HostWindowState newState);
        Task SetCursorVisibilityAsync(bool isVisible);
        Task SetChildModalityAsync(bool isModal);
        Task SetDesignerModeAsync(bool enabled);
        Task UnloadControlAsync();

        /// <summary>
        /// Captures a screenshot of the child window. Returns a platform-specific image object
        /// (e.g. BitmapSource on WPF) or null if unavailable.
        /// </summary>
        object? CaptureChildScreenshot();

        bool HasLoadedControl { get; }
        void HideChild();
        void ShowChild();

        event Func<CrashReason, ErrorMessage, Task> ChildProcessCrashed;
        event Action<LogMessage> ChildLogReceived;
        event Action<DesignerSelectionMessage> DesignerSelectionReceived;
    }
}
