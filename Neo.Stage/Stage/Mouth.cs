using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using NAudio;
using NAudio.Wave;

namespace Neo.Stage;

/// <summary>
/// The stage's voice: ElevenLabs text to speech, streamed and played as it arrives. It can begin before the answer is
/// written to its end: <see cref="Add"/> takes the answer as it comes, and each finished sentence goes to the voice at
/// once while the next is still being written. One answer at a time; a new one, or <see cref="Stop"/>, silences the last.
/// </summary>
internal sealed class Mouth
{
    /// <summary>The voice to speak with; without it a calm stock voice stands in until the stage has its own.</summary>
    public const string VoiceVariable = "NEO_STAGE_VOICE";
    private const string StandIn = "JBFqnCBsd6RMkjVDRZzb"; // "George", an ElevenLabs stock voice
    internal const int SampleRate = 22050;
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly string _key, _voice, _model;
    private readonly Func<IWaveProvider, IWavePlayer?> _player;
    private Speech? _speech;

    /// <param name="player">Where the sound goes; a check passes one that only counts it. Null: the loudspeaker.</param>
    public Mouth(string key, string? voice = null, string model = "eleven_multilingual_v2", Func<IWaveProvider, IWavePlayer?>? player = null)
    {
        _key = key;
        _voice = voice ?? Variable(VoiceVariable) ?? StandIn;
        _model = model;
        _player = player ?? (_ => new WaveOutEvent { DesiredLatency = 120 });
    }

    /// <summary>What went wrong the last time, if anything.</summary>
    public string? Problem { get; private set; }

    /// <summary>How loud the voice is right now, from 0 to 1.</summary>
    public double Level => _speech?.Level ?? 0;

    public bool Speaking => _speech is { Done.IsCompleted: false };

    /// <summary>A new answer begins; what was still being said stops.</summary>
    public void Begin()
    {
        Stop();
        Problem = null;
        _speech = new Speech(this);
    }

    /// <summary>A piece of the answer as it is written. Whole sentences go to the voice at once.</summary>
    public void Add(string text) => _speech?.Add(text);

    /// <summary>A piece of the answer is complete (a tool comes next, say): what is left of it goes to the voice too.</summary>
    public void Flush() => _speech?.Flush();

    /// <summary>The answer is complete; returns when it has been heard to the end, or was stopped.</summary>
    public Task FinishAsync()
    {
        if (_speech is not { } speech)
            return Task.CompletedTask;
        speech.Finish();
        return speech.Done;
    }

    /// <summary>Speaks <paramref name="text"/> and returns when it has been heard to the end, or was stopped.</summary>
    public Task SpeakAsync(string text)
    {
        Begin();
        Add(text);
        return FinishAsync();
    }

    public void Stop() => _speech?.Stop();

    private static string? Variable(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } own ? own
            : OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) : null;

    /// <summary>
    /// Where a sentence ends in <paramref name="text"/>: after . ! ? or … followed by a space, but not after a number
    /// ("17. November") or a short abbreviation ("z. B.", "ca."). -1 when no sentence is complete yet.
    /// </summary>
    internal static int SentenceEnd(string text)
    {
        for (var i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] is not ('.' or '!' or '?' or '…') || !char.IsWhiteSpace(text[i + 1]))
                continue;
            if (text[i] == '.')
            {
                var start = i;
                while (start > 0 && char.IsLetterOrDigit(text[start - 1]))
                    start--;
                var word = text[start..i];
                if (word.Length == 0 || word.All(char.IsDigit) || s_abbreviations.Contains(word.ToLowerInvariant()))
                    continue;
            }
            return i + 1;
        }
        return -1;
    }

    private static readonly HashSet<string> s_abbreviations =
        ["z", "b", "d", "h", "u", "a", "o", "ä", "s", "v", "nr", "dr", "ca", "bzw", "usw", "evtl", "ggf", "vgl", "inkl", "bspw", "etc", "max", "min", "std", "tel", "str", "mio", "mrd", "jh", "co", "st"];

    /// <summary>One answer being spoken: sentences in, sound out, in order, through one player.</summary>
    private sealed class Speech
    {
        private readonly Mouth _mouth;
        private readonly Channel<string> _sentences = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        private readonly CancellationTokenSource _stop = new();
        private readonly StringBuilder _pending = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private Meter? _meter;

        public Speech(Mouth mouth)
        {
            _mouth = mouth;
            Done = Task.Run(RunAsync);
        }

        public Task Done { get; }

        public double Level => _meter?.Level ?? 0;

        public void Add(string text)
        {
            _pending.Append(text);
            int end;
            while ((end = SentenceEnd(_pending.ToString())) > 0)
            {
                Say(_pending.ToString(0, end));
                _pending.Remove(0, end);
            }
        }

        public void Flush()
        {
            Say(_pending.ToString());
            _pending.Clear();
        }

        public void Finish()
        {
            Flush();
            _sentences.Writer.TryComplete();
        }

        public void Stop()
        {
            try { _stop.Cancel(); }
            catch (ObjectDisposedException) { }
            _sentences.Writer.TryComplete();
        }

        private void Say(string sentence)
        {
            if (sentence.Trim().Length > 0)
                _sentences.Writer.TryWrite(sentence.Trim());
        }

        private async Task RunAsync()
        {
            var sound = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, 1)) { BufferDuration = TimeSpan.FromMinutes(3), ReadFully = true };
            _meter = new Meter(sound);
            IWavePlayer? output = null;
            var playing = false;
            var said = new StringBuilder();
            var sentences = 0;
            long received = 0;
            try
            {
                output = _mouth._player(_meter);
                output?.Init(_meter);
                await foreach (var sentence in _sentences.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
                {
                    sentences++;
                    using var request = new HttpRequestMessage(HttpMethod.Post,
                        $"https://api.elevenlabs.io/v1/text-to-speech/{_mouth._voice}/stream?output_format=pcm_{SampleRate}");
                    request.Headers.Add("xi-api-key", _mouth._key);
                    // What was said before lets the voice go on in the same tone instead of starting afresh.
                    var body = new JsonObject { ["text"] = sentence, ["model_id"] = _mouth._model };
                    if (said.Length > 0)
                        body["previous_text"] = said.Length <= 600 ? said.ToString() : said.ToString(said.Length - 600, 600);
                    request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                    using var response = await s_http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _stop.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        _mouth.Problem = $"text to speech: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(_stop.Token).ConfigureAwait(false)}";
                        App.Log("voice: " + _mouth.Problem);
                        continue;
                    }
                    await using var stream = await response.Content.ReadAsStreamAsync(_stop.Token).ConfigureAwait(false);
                    var chunk = new byte[8192];
                    int read;
                    while ((read = await stream.ReadAsync(chunk, _stop.Token).ConfigureAwait(false)) > 0)
                    {
                        sound.AddSamples(chunk, 0, read);
                        received += read;
                        if (!playing && sound.BufferedDuration >= TimeSpan.FromMilliseconds(250))
                        {
                            output?.Play();
                            playing = true;
                            App.Log($"voice: first sound after {_clock.Elapsed.TotalSeconds:0.00} s");
                        }
                    }
                    said.Append(said.Length > 0 ? " " : "").Append(sentence);
                }
                if (!playing && sound.BufferedBytes > 0)
                {
                    output?.Play();
                    playing = true;
                }
                // Until all of it has been heard: the buffer, then the device's own.
                while (output != null && sound.BufferedBytes > 0)
                    await Task.Delay(40, _stop.Token).ConfigureAwait(false);
                if (output != null)
                    await Task.Delay(150, _stop.Token).ConfigureAwait(false);
                App.Log($"voice: done after {_clock.Elapsed.TotalSeconds:0.0} s, {sentences} pieces, {received / (2.0 * SampleRate):0.0} s of speech");
            }
            catch (OperationCanceledException)
            {
                App.Log($"voice: {(_stop.IsCancellationRequested ? "stopped" : "timed out")} after {_clock.Elapsed.TotalSeconds:0.0} s{(playing ? "" : ", before any sound")}");
            }
            catch (HttpRequestException exception)
            {
                _mouth.Problem = "text to speech: " + exception.Message;
                App.Log("voice: " + _mouth.Problem);
            }
            catch (MmException exception)
            {
                _mouth.Problem = "no loudspeaker: " + exception.Message;
                App.Log("voice: " + _mouth.Problem);
            }
            finally
            {
                output?.Stop();
                output?.Dispose();
                _meter.Silent = true;
            }
        }
    }

    /// <summary>Passes the sound on and measures how loud it is as the player takes it.</summary>
    private sealed class Meter(IWaveProvider source) : IWaveProvider
    {
        private double _level;

        public WaveFormat WaveFormat => source.WaveFormat;

        public double Level => Silent ? 0 : Volatile.Read(ref _level);

        public bool Silent { get; set; }

        public int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            double sum = 0;
            var samples = 0;
            for (var i = offset; i + 1 < offset + read; i += 2)
            {
                var sample = (short)(buffer[i] | (buffer[i + 1] << 8)) / 32768.0;
                sum += sample * sample;
                samples++;
            }
            Volatile.Write(ref _level, samples > 0 ? Math.Min(1, Math.Sqrt(sum / samples) * 4) : 0);
            return read;
        }
    }
}
