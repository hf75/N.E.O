using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace Neo.App.WebApp.Browser;

[SupportedOSPlatform("browser")]
public static partial class MonacoBridge
{
    private static bool _imported;

    [JSImport("neo_monaco_show", "neo-monaco.js")]
    public static partial Task<bool> ShowAsync(double x, double y, double width, double height, string initialCode);

    [JSImport("neo_monaco_reposition", "neo-monaco.js")]
    public static partial void Reposition(double x, double y, double width, double height);

    [JSImport("neo_monaco_hide", "neo-monaco.js")]
    public static partial void Hide();

    [JSImport("neo_monaco_set_code", "neo-monaco.js")]
    public static partial void SetCode(string code);

    [JSImport("neo_monaco_get_code", "neo-monaco.js")]
    public static partial string GetCode();

    [JSImport("neo_monaco_dispose", "neo-monaco.js")]
    public static partial void Dispose();

    [JSImport("neo_trigger_download", "neo-storage.js")]
    public static partial void TriggerDownload(string filename, string text);

    public static async Task EnsureImportedAsync()
    {
        if (_imported) return;
        // IMPORTANT: JSHost.ImportAsync resolves relative module specifiers
        // against the runtime loader's base, which is wwwroot/_framework/.
        // Our JS files live at wwwroot/, so we step one level up.
        await JSHost.ImportAsync("neo-monaco.js", "../neo-monaco.js");
        await JSHost.ImportAsync("neo-storage.js", "../neo-storage.js");
        _imported = true;
    }
}
