using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Neo.Stage;

/// <summary>
/// Opens what a person names: a website, a folder they know by name (Downloads, Dokumente…), a file or folder by its
/// path, or a program as the Start menu lists it. Opening changes nothing on the computer, so it needs no "yes"; the
/// sentence that comes back says what was opened, or what came into question when the name was not clear.
/// </summary>
internal static class Opener
{
    /// <param name="hint">What the title of the window it opens will hold (for bringing it to the front); null when nothing was opened.</param>
    public static string Open(string what, bool pretend, out string? hint)
    {
        hint = null;
        what = what.Trim().Trim('"', '„', '“');
        if (what.Length == 0)
            return "Was soll ich öffnen?";

        if (Website(what) is { } address)
        {
            // "www.spiegel.de/news" → "spiegel": pages are titled by their site's name, not its address.
            var host = new Uri(address).Host.Split('.');
            hint = host.Length >= 2 ? host[^2] : host[0];
            return Start(address, $"die Webseite {address}", pretend);
        }
        if (KnownFolder(what) is { } folder)
        {
            hint = Path.GetFileName(folder);
            return Start(folder, $"der Ordner {hint}", pretend, explorer: true);
        }
        if (LooksLikePath(what))
        {
            var path = Environment.ExpandEnvironmentVariables(what);
            if (Directory.Exists(path))
            {
                hint = Path.GetFileName(path.TrimEnd('\\'));
                return Start(path, $"der Ordner {hint}", pretend, explorer: true);
            }
            if (File.Exists(path))
            {
                hint = Path.GetFileNameWithoutExtension(path);
                return Start(path, $"die Datei {Path.GetFileName(path)}", pretend);
            }
            return $"Den Pfad {what} gibt es nicht.";
        }

        var apps = StartMenuApps.Matching(what);
        if (apps.Count == 0)
            return $"Ein Programm „{what}“ finde ich nicht im Startmenü.";
        if (apps.Count > 1 && apps[0].Score == apps[1].Score)
            return $"Mehrere Programme passen zu „{what}“: {string.Join(", ", apps.Take(6).Select(app => app.Name))}. Welches ist gemeint?";
        var best = apps[0];
        hint = best.Name;
        return Start($"shell:AppsFolder\\{best.Id}", best.Name, pretend, explorer: true);
    }

    private static string Start(string target, string name, bool pretend, bool explorer = false)
    {
        if (pretend)
            return $"Nur eine Probe, nichts wurde geöffnet. Sonst wäre jetzt offen: {name}.";
        try
        {
            using var _ = explorer
                ? Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { target }, UseShellExecute = false })
                : Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return $"{Capital(name)} ist geöffnet.";
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"{Capital(name)} ließ sich nicht öffnen: {exception.Message}";
        }
    }

    private static string Capital(string text) => text.Length > 0 ? char.ToUpper(text[0], CultureInfo.GetCultureInfo("de-DE")) + text[1..] : text;

    // "spiegel.de", "www.example.org/news", "https://…": a website. A word with a dot that is a file name is not.
    private static string? Website(string what)
    {
        if (what.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || what.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return what;
        if (what.Contains(' ') || what.Contains('\\') || LooksLikePath(what))
            return null;
        var host = what.Split('/')[0];
        var dot = host.LastIndexOf('.');
        if (dot <= 0 || dot == host.Length - 1)
            return null;
        var ending = host[(dot + 1)..].ToLowerInvariant();
        string[] files = ["exe", "docx", "doc", "xlsx", "xls", "pptx", "pdf", "txt", "png", "jpg", "jpeg", "zip", "md", "cs", "json", "neo"];
        return ending.All(char.IsLetter) && ending.Length is >= 2 and <= 12 && !files.Contains(ending) ? "https://" + what : null;
    }

    private static bool LooksLikePath(string what)
        => what.Length > 2 && (what[1] == ':' || what.StartsWith(@"\\", StringComparison.Ordinal) || what.StartsWith('%'));

    // The folders a person names without a path, in German and English.
    private static string? KnownFolder(string what)
    {
        var name = what.ToLowerInvariant().Replace("ordner", "").Replace("der ", "").Replace("die ", "").Replace("meine ", "").Trim();
        return name switch
        {
            "downloads" or "download" or "heruntergeladene dateien" => KnownFolderPath(s_downloads),
            "dokumente" or "documents" or "eigene dateien" or "eigene dokumente" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "bilder" or "pictures" or "fotos" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "desktop" or "schreibtisch" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "musik" or "music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "benutzerordner" or "persönlicher" or "home" or "profil" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            _ => null,
        } is { Length: > 0 } path && Directory.Exists(path) ? path : null;
    }

    private static readonly Guid s_downloads = new("374DE290-123F-4565-9164-39C4925E467B");

    private static string? KnownFolderPath(Guid folder)
    {
        if (SHGetKnownFolderPath(folder, 0, IntPtr.Zero, out var path) != 0)
            return null;
        try { return Marshal.PtrToStringUni(path); }
        finally { Marshal.FreeCoTaskMem(path); }
    }

    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid folder, uint flags, IntPtr token, out IntPtr path);
}

/// <summary>
/// The programs as the Start menu lists them (the Applications folder of the shell): desktop programs and store apps
/// alike, by the names this Windows shows, read once and again after ten minutes.
/// </summary>
internal static class StartMenuApps
{
    private static readonly object s_gate = new();
    private static IReadOnlyList<(string Name, string Id)> s_all = [];
    private static DateTime s_read;

    public sealed record Match(string Name, string Id, int Score);

    /// <summary>The programs whose name fits, the best first.</summary>
    public static IReadOnlyList<Match> Matching(string name)
    {
        var wanted = Simple(name);
        if (wanted.Length == 0)
            return [];
        var words = wanted.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return All()
            .Where(app => !app.Name.Contains("deinstall", StringComparison.OrdinalIgnoreCase) && !app.Name.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
            .Select(app =>
            {
                var have = Simple(app.Name);
                var score = have == wanted ? 100
                    : have.StartsWith(wanted + " ", StringComparison.Ordinal) ? 85
                    : (" " + have + " ").Contains(" " + wanted + " ", StringComparison.Ordinal) ? 75
                    : words.All(word => have.Contains(word, StringComparison.Ordinal)) ? 60
                    : (" " + wanted + " ").Contains(" " + have + " ", StringComparison.Ordinal) && have.Length >= 3 ? 50
                    : 0;
                return new Match(app.Name, app.Id, score);
            })
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Name.Length)
            .ToList();
    }

    public static IReadOnlyList<(string Name, string Id)> All()
    {
        lock (s_gate)
        {
            if (s_all.Count > 0 && DateTime.Now - s_read < TimeSpan.FromMinutes(10))
                return s_all;
        }
        // The shell's objects want a single-threaded apartment of their own.
        IReadOnlyList<(string Name, string Id)> read = [];
        var thread = new Thread(() =>
        {
            try { read = Read(); }
            catch (Exception exception) when (exception is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                App.Log("apps: " + exception.Message);
            }
        }) { IsBackground = true, Name = "Neo.Stage: apps" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(10));
        lock (s_gate)
        {
            if (read.Count > 0)
            {
                s_all = read;
                s_read = DateTime.Now;
            }
            return s_all;
        }
    }

    private static List<(string Name, string Id)> Read()
    {
        var found = new List<(string Name, string Id)>();
        if (Type.GetTypeFromCLSID(new Guid("13709620-C279-11CE-A49E-444553540000")) is not { } type) // Shell.Application
            return found;
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic folder = shell.NameSpace("shell:::{4234d49b-0245-4df3-b780-3893943456e1}"); // the Applications folder
            dynamic items = folder.Items();
            int count = items.Count;
            for (var i = 0; i < count; i++)
            {
                dynamic item = items.Item(i);
                string? name = item.Name;
                string? id = item.Path;
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id))
                    found.Add((name.Trim(), id));
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
        return found;
    }

    // Lower case, without accents and signs: "Microsoft Word 2021" → "microsoft word 2021".
    private static string Simple(string text)
    {
        var plain = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            plain.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return string.Join(' ', plain.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
