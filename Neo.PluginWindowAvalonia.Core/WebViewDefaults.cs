using System;
using System.IO;
using System.Runtime.CompilerServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Neo.PluginWindowAvalonia
{
    /// <summary>
    /// Generated apps may embed the <see cref="NativeWebView"/> that ships with this runtime. On
    /// Windows its WebView2 backend keeps the browser profile in "&lt;exe&gt;.WebView2" next to the
    /// executable unless every single control is told otherwise — that is this process' bin
    /// folder: not writable once installed, and Neo.App's build copies that folder wholesale.
    ///
    /// <para>The control has no process-wide setting and Avalonia's WebView2 loader ignores the
    /// WEBVIEW2_USER_DATA_FOLDER variable, so the host answers <c>EnvironmentRequested</c> for
    /// every web view a plugin creates. A control gets its logical parent before it is attached
    /// to the visual tree, and the browser is only created after that, so hooking the parent
    /// change is early enough. Generated code that sets its own folder still wins.</para>
    /// </summary>
    internal static class WebViewDefaults
    {
        private static readonly ConditionalWeakTable<NativeWebView, object> Hooked = new();
        private static bool _installed;

        /// <summary>Per-user browser profile shared by all preview windows.</summary>
        public static string UserDataFolder { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Neo", "WebView2");

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Install()
        {
            if (_installed) return;
            _installed = true;

            StyledElement.ParentProperty.Changed.AddClassHandler<NativeWebView>((webView, _) => Hook(webView));
            Visual.VisualParentProperty.Changed.AddClassHandler<NativeWebView>((webView, _) => Hook(webView));
        }

        private static void Hook(NativeWebView webView)
        {
            if (Hooked.TryGetValue(webView, out _)) return;
            Hooked.Add(webView, new object());
            webView.EnvironmentRequested += OnEnvironmentRequested;
        }

        private static void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
        {
            if (e is not WindowsWebView2EnvironmentRequestedEventArgs webView2) return;
            if (!string.IsNullOrEmpty(webView2.UserDataFolder)) return; // the app chose its own

            try
            {
                Directory.CreateDirectory(UserDataFolder);
                webView2.UserDataFolder = UserDataFolder;
            }
            catch
            {
                // WebView2 falls back to its default location.
            }
        }
    }
}
