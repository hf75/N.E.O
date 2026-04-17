using System;
using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Browser;
using Neo.App.WebApp;
using Neo.App.WebApp.Browser;
using Neo.App.WebApp.Views;

internal sealed partial class Program
{
    private static async Task Main(string[] args)
    {
        App.BackendBaseUri = ResolveBaseUri();
        App.SessionStore = new IndexedDbSessionStore();

        // Hook .neo download trigger into the MainView when it's created.
        App.MainViewConfigured += ConfigureForBrowser;

        await BuildAvaloniaApp()
            .WithInterFont()
            .StartBrowserAppAsync("out");
    }

    private static void ConfigureForBrowser(MainView view)
    {
        view.SessionJsonReady += (name, json) =>
        {
            try
            {
                // Fire-and-forget; MonacoBridge.TriggerDownload needs the JS module imported.
                _ = InvokeDownloadAsync(name, json);
            }
            catch { /* ignore in POC */ }
        };

        MonacoWiring.Attach(view);
    }

    private static async Task InvokeDownloadAsync(string name, string json)
    {
        await MonacoBridge.EnsureImportedAsync();
        MonacoBridge.TriggerDownload(name, json);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>();

    private static Uri ResolveBaseUri()
    {
        if (!OperatingSystem.IsBrowser())
            return new Uri("http://localhost/", UriKind.Absolute);
        try
        {
            var location = JSHost.GlobalThis.GetPropertyAsJSObject("location");
            var origin = location?.GetPropertyAsString("origin");
            if (!string.IsNullOrEmpty(origin))
                return new Uri(origin.EndsWith("/") ? origin : origin + "/", UriKind.Absolute);
        }
        catch { /* fall through */ }
        return new Uri("http://localhost/", UriKind.Absolute);
    }
}
