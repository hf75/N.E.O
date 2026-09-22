using System.Diagnostics;
using System.Text.Json;

namespace Neo.App;

/// <summary>
/// Where running apps announce themselves: one JSON file per published app in a folder every
/// N.E.O. process on the machine can read. An entry whose process is gone is dropped on the next
/// listing, so a crashed app leaves no ghost.
/// </summary>
internal static class AppRegistry
{
    public const string FolderEnvironmentVariable = "NEO_APP_NETWORK_DIR";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The registry folder; <c>NEO_APP_NETWORK_DIR</c> overrides it (tests, isolated setups).</summary>
    public static string Folder
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(FolderEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local)) local = Path.GetTempPath();
            return Path.Combine(local, "Neo", "AppNetwork");
        }
    }

    public static string PathFor(string id) => Path.Combine(Folder, id + ".json");

    public static void Write(AppDescriptor app)
    {
        Directory.CreateDirectory(Folder);
        var path = PathFor(app.Id);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(app, Json));
        File.Move(temp, path, overwrite: true);
    }

    public static void Remove(string id)
    {
        try { File.Delete(PathFor(id)); } catch { /* already gone */ }
    }

    /// <summary>Every published app whose process is still alive.</summary>
    public static List<AppDescriptor> List()
    {
        var result = new List<AppDescriptor>();
        if (!Directory.Exists(Folder)) return result;
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
        {
            AppDescriptor? app = null;
            try { app = JsonSerializer.Deserialize<AppDescriptor>(File.ReadAllText(file), Json); }
            catch { /* half-written or foreign file: skip it */ }
            if (app == null) continue;
            if (!IsAlive(app.ProcessId))
            {
                try { File.Delete(file); } catch { }
                continue;
            }
            result.Add(app);
        }
        return result.OrderBy(a => a.PublishedAt).ToList();
    }

    private static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }
}
