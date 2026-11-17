using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using NAudio.Wave;

namespace Neo.Stage;

/// <summary>A demo as a list of lines a person says, in order, with a pause after each answer.</summary>
internal sealed record DemoScript(string Language, string Voice, IReadOnlyList<DemoLine> Lines, bool Record = true, string Model = "eleven_multilingual_v2");

internal sealed record DemoLine(string Say, double Pause = 2);

/// <summary>
/// Plays a demo without anyone at the computer. The lines are spoken by a synthetic voice (ElevenLabs), played aloud and
/// heard by the stage as if a person held the key and said them; the stage answers live with its own voice and its own
/// hands. Nothing touches the real keyboard or mouse; the pointer is put in a corner once and stays there. A calm backdrop
/// hides the desktop. With recording, ffmpeg takes the work area of the screen, and both voices are kept as they were
/// played, with their times, for putting the sound under the picture afterwards (take.json).
/// </summary>
internal sealed class Director
{
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<JsonObject> _lines = [], _voice = [], _waits = [];
    private readonly string _folder;
    private volatile bool _holding;
    private byte[] _clip = [];
    private TaskCompletionSource _heard = new(), _spoken = new();
    private WaveOutEvent? _speaker;
    private JsonObject? _waiting;

    private Director(string folder) => _folder = folder;

    private double Now => _clock.Elapsed.TotalSeconds;

    public static async Task RunAsync(string scriptPath, Screen screen)
    {
        var script = JsonSerializer.Deserialize<DemoScript>(await File.ReadAllTextAsync(scriptPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("No demo in " + scriptPath);
        Language.Code = script.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "de";
        var folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(scriptPath))!, "take-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(folder);
        await new Director(folder).PlayAsync(script, screen, Path.GetDirectoryName(Path.GetFullPath(scriptPath))!);
    }

    private async Task PlayAsync(DemoScript script, Screen screen, string home)
    {
        var key = Ears.Key() ?? throw new InvalidOperationException($"No {Ears.KeyVariable}.");
        App.Log($"demo: {script.Lines.Count} lines into {_folder}");

        // The person's lines, made once and kept by their words.
        var clips = new List<byte[]>();
        foreach (var line in script.Lines)
            clips.Add(await SpokenAsync(key, script.Voice, script.Model, line.Say, Path.Combine(home, "lines")));

        // The stage as it is at work, with ears that hear the clips, a voice that is taped, and a brain that sees only the
        // windows of the apps N.E.O. builds.
        var stage = new StageOverlay(screen);
        var brain = new Brain(only: (_, program) => program.Contains("PluginWindow", StringComparison.OrdinalIgnoreCase));
        brain.Working += what => Dispatcher.UIThread.Post(() => Working(what));
        _ = brain.WarmAsync();
        var mouth = new Mouth(key)
        {
            Clock = () => _clock.Elapsed,
            Tape = (began, sound) => Dispatcher.UIThread.Post(() => KeepVoice(began, sound)),
        };
        var live = new LiveStage(stage, () => _holding, Hear, brain, mouth);
        live.Spoken += _ => _spoken.TrySetResult();
        live.Start();

        // Nothing of the desktop: the backdrop in front of every window, the pointer resting in a corner.
        var backdrop = new Backdrop(screen);
        backdrop.Show();
        await Task.Delay(300);
        Hands.Front(backdrop.Handle);
        var area = screen.WorkingArea;
        SetCursorPos(area.Right - 60, area.Bottom - 46);
        await Task.Delay(1500);

        Process? recorder = null;
        if (script.Record)
            recorder = StartRecording(area.X, area.Y, area.Width, area.Height);
        var recordAsked = Now;
        await Task.Delay(2500);

        for (var i = 0; i < script.Lines.Count; i++)
        {
            while (live.Busy)
                await Task.Delay(100);
            _clip = clips[i];
            _heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _spoken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            App.Log($"demo: line {i + 1}: {script.Lines[i].Say}");
            _holding = true; // the key, held as a person would
            await _heard.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var started = Now;
            await Task.Delay(TimeSpan.FromSeconds(_clip.Length / 32000.0 + 0.35));
            _holding = false;
            _lines.Add(new JsonObject { ["text"] = script.Lines[i].Say, ["start"] = started, ["end"] = started + _clip.Length / 32000.0 });
            try { await _spoken.Task.WaitAsync(TimeSpan.FromMinutes(4)); }
            catch (TimeoutException) { App.Log($"demo: line {i + 1} had no end in time"); }
            await Task.Delay(TimeSpan.FromSeconds(script.Lines[i].Pause));
        }

        await Task.Delay(1500);
        var end = Now;
        if (recorder != null)
            await StopRecordingAsync(recorder);
        backdrop.Close();

        var take = new JsonObject
        {
            ["recordAsked"] = recordAsked,
            ["end"] = end,
            ["area"] = new JsonObject { ["x"] = area.X, ["y"] = area.Y, ["width"] = area.Width, ["height"] = area.Height },
            ["lines"] = new JsonArray([.. _lines]),
            ["voice"] = new JsonArray([.. _voice]),
            ["waits"] = new JsonArray([.. _waits]),
        };
        await File.WriteAllTextAsync(Path.Combine(_folder, "take.json"), take.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        App.Log($"demo: done after {end:0.0} s, take in {_folder}");
        await brain.DisposeAsync();
    }

    // The stage starts listening: the person's clip plays aloud from this moment, and the ears hear the same sound.
    private Ears? Hear(string key)
    {
        _speaker?.Dispose();
        var file = Path.Combine(_folder, $"line-{_lines.Count + 1}.pcm");
        File.WriteAllBytes(file, _clip);
        _speaker = new WaveOutEvent { DesiredLatency = 100 };
        _speaker.Init(new RawSourceWaveStream(new MemoryStream(_clip), new WaveFormat(16000, 16, 1)));
        _speaker.Play();
        _heard.TrySetResult();
        return Ears.Hear(key, _clip, Language.Code);
    }

    private void KeepVoice(TimeSpan began, byte[] sound)
    {
        var file = Path.Combine(_folder, $"voice-{_voice.Count + 1}.pcm");
        File.WriteAllBytes(file, sound);
        _voice.Add(new JsonObject { ["start"] = began.TotalSeconds, ["file"] = Path.GetFileName(file), ["seconds"] = sound.Length / (2.0 * Mouth.SampleRate) });
        App.Log($"demo: voice {_voice.Count} at {began.TotalSeconds:0.00} s, {sound.Length / (2.0 * Mouth.SampleRate):0.0} s");
    }

    // What takes a while (N.E.O. building), for speeding it up in the video.
    private void Working(string? what)
    {
        if (what != null)
        {
            _waiting = new JsonObject { ["label"] = what, ["from"] = Now };
            return;
        }
        if (_waiting == null)
            return;
        _waiting["to"] = Now;
        _waits.Add(_waiting);
        _waiting = null;
    }

    private static async Task<byte[]> SpokenAsync(string key, string voice, string model, string text, string folder)
    {
        Directory.CreateDirectory(folder);
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(voice + "|" + model + "|" + text)))[..16] + ".pcm";
        var file = Path.Combine(folder, name);
        if (File.Exists(file))
            return await File.ReadAllBytesAsync(file);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.elevenlabs.io/v1/text-to-speech/{voice}?output_format=pcm_16000");
        request.Headers.Add("xi-api-key", key);
        request.Content = new StringContent(new JsonObject { ["text"] = text, ["model_id"] = model }.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await s_http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"text to speech: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        var sound = await response.Content.ReadAsByteArrayAsync();
        // A little quiet before and after, as when a person begins and ends a sentence.
        var padded = new byte[3200 + sound.Length + 4800];
        Buffer.BlockCopy(sound, 0, padded, 3200, sound.Length);
        await File.WriteAllBytesAsync(file, padded);
        return padded;
    }

    private Process StartRecording(int x, int y, int width, int height)
    {
        var ffmpeg = Ffmpeg();
        var start = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _folder,
        };
        foreach (var argument in (string[])["-y", "-hide_banner", "-loglevel", "error", "-f", "gdigrab", "-framerate", "30", "-offset_x", $"{x}", "-offset_y", $"{y}",
                     "-video_size", $"{width / 2 * 2}x{height / 2 * 2}", "-draw_mouse", "1", "-i", "desktop", "-c:v", "libx264", "-preset", "ultrafast", "-qp", "16",
                     "-pix_fmt", "yuv420p", "raw.mkv"])
            start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg did not start.");
        _ = process.StandardError.ReadToEndAsync().ContinueWith(errors =>
        {
            if (errors.Result.Trim().Length > 0)
                App.Log("ffmpeg: " + errors.Result.Trim());
        }, TaskScheduler.Default);
        App.Log($"demo: recording {width}x{height} at {x},{y} with {ffmpeg}");
        return process;
    }

    private static async Task StopRecordingAsync(Process recorder)
    {
        try
        {
            await recorder.StandardInput.WriteAsync("q");
            await recorder.StandardInput.FlushAsync();
            await recorder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException)
        {
            App.Log("demo: ffmpeg did not stop by itself: " + exception.Message);
            try { recorder.Kill(); }
            catch (InvalidOperationException) { }
        }
    }

    private static string Ffmpeg()
    {
        var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        return File.Exists(winget) ? winget : "ffmpeg";
    }

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
}
