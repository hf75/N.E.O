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
        Task UnloadControlAsync();

        /// <summary>
        /// What the runtime of the child can patch into a loaded plugin, as reported in its Hello message.
        /// Null: this child cannot hot reload (older build, or started without DOTNET_MODIFIABLE_ASSEMBLIES).
        /// </summary>
        string? HotReloadCapabilities { get; }

        /// <summary>
        /// Asks the child to patch the running plugin in place. Null: no answer.
        /// The files among <paramref name="dependencyFiles"/> the child has not received yet (a
        /// NuGet package added with this change) are streamed to it first.
        /// </summary>
        Task<HotReloadResultMessage?> HotReloadAsync(HotReloadRequest request, IEnumerable<string> dependencyFiles);

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
    }
}
