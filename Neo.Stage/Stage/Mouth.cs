using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using NAudio;
using NAudio.Wave;

namespace Neo.Stage;

/// <summary>
/// The stage's voice: ElevenLabs text to speech, streamed and played as it arrives (the first sound after about a
/// third of a second). One answer at a time; a new one, or <see cref="Stop"/>, silences the last.
/// </summary>
internal sealed class Mouth
{
    /// <summary>The voice to speak with; without it a calm stock voice stands in until the stage has its own.</summary>
    public const string VoiceVariable = "NEO_STAGE_VOICE";
    private const string StandIn = "JBFqnCBsd6RMkjVDRZzb"; // "George", an ElevenLabs stock voice
    private const int SampleRate = 22050;
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly string _key, _voice, _model;
    private CancellationTokenSource? _speaking;

    public Mouth(string key, string? voice = null, string model = "eleven_multilingual_v2")
    {
        _key = key;
        _voice = voice ?? Variable(VoiceVariable) ?? StandIn;
        _model = model;
    }

    /// <summary>What went wrong the last time, if anything.</summary>
    public string? Problem { get; private set; }

    public bool Speaking => _speaking != null;

    /// <summary>Speaks <paramref name="text"/> and returns when it has been heard to the end, or was stopped.</summary>
    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        Stop();
        if (string.IsNullOrWhiteSpace(text))
            return;
        var speaking = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _speaking = speaking;
        var sound = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, 1)) { BufferDuration = TimeSpan.FromMinutes(2) };
        var output = new WaveOutEvent { DesiredLatency = 120 };
        try
        {
            output.Init(sound);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.elevenlabs.io/v1/text-to-speech/{_voice}/stream?output_format=pcm_{SampleRate}");
            request.Headers.Add("xi-api-key", _key);
            request.Content = new StringContent(new JsonObject { ["text"] = text, ["model_id"] = _model }.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await s_http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, speaking.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Problem = $"text to speech: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(speaking.Token).ConfigureAwait(false)}";
                return;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(speaking.Token).ConfigureAwait(false);
            var chunk = new byte[8192];
            var playing = false;
            int read;
            while ((read = await stream.ReadAsync(chunk, speaking.Token).ConfigureAwait(false)) > 0)
            {
                sound.AddSamples(chunk, 0, read);
                if (!playing && sound.BufferedDuration >= TimeSpan.FromMilliseconds(250))
                {
                    output.Play();
                    playing = true;
                }
            }
            if (!playing)
                output.Play();
            while (sound.BufferedBytes > 0)
                await Task.Delay(40, speaking.Token).ConfigureAwait(false);
            await Task.Delay(120, speaking.Token).ConfigureAwait(false); // the device's own buffer
            Problem = null;
        }
        catch (OperationCanceledException) { }
        catch (HttpRequestException exception)
        {
            Problem = "text to speech: " + exception.Message;
        }
        catch (MmException exception)
        {
            Problem = "no loudspeaker: " + exception.Message; // no playback device
        }
        finally
        {
            output.Stop();
            output.Dispose();
            if (_speaking == speaking)
                _speaking = null;
            speaking.Dispose();
        }
    }

    public void Stop()
    {
        try { _speaking?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private static string? Variable(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } own ? own
            : OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) : null;
}
