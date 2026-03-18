namespace Neo.App
{
    /// <summary>
    /// Immutable Sandbox-Konfiguration (cross-platform).
    /// </summary>
    public sealed record SandboxSettings
    {
        public bool AllowNetworkAccess { get; init; } = false;
        public bool AllowClipboardAccess { get; init; } = false;
        public bool AllowUserFileAccess { get; init; } = false;
        public bool AllowPrinting { get; init; } = false;
        public bool AllowWebcamAndMicrophone { get; init; } = false;
        public bool AllowSystemSettingsChanges { get; init; } = false;

        public List<string> GrantedFolders { get; set; } = new List<string>();

        public static SandboxSettings AllowAll => new() { AllowNetworkAccess = true, AllowClipboardAccess = true, AllowUserFileAccess = true, AllowPrinting = true, AllowWebcamAndMicrophone = true, AllowSystemSettingsChanges = true };
        public static SandboxSettings MaximumSecurity => new();
        public static SandboxSettings InternetClient => new() { AllowNetworkAccess = true };
        public static SandboxSettings LocalDocumentEditor => new() { AllowUserFileAccess = true, AllowClipboardAccess = true };
    }
}
