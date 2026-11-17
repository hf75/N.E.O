using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using NAudio.Wave;

namespace Neo.Stage;

/// <summary>A word as it was spoken: seconds from the moment listening began.</summary>
internal sealed record HeardWord(string Text, double Start, double End);

/// <summary>What was heard. <see cref="Final"/>: the recognition's last word on it, with the time of every word.</summary>
internal sealed record Heard(string Text, IReadOnlyList<HeardWord> Words, bool Final);

/// <summary>
/// The stage's ears: the microphone, streamed to ElevenLabs' realtime speech recognition (Scribe) only while the key is
/// held. Partial text comes while the person speaks; letting go commits, and the final text comes with a start and end
/// time per word, counted from the first sound sent, which is the moment listening began. Recording starts at once and
/// the connection opens meanwhile, so the first word is not lost.
/// </summary>
internal sealed class Ears : IAsyncDisposable
{
    public const string KeyVariable = "ELEVENLABS_API_KEY";
    private const int SampleRate = 16000, ChunkBytes = SampleRate * 2 / 10; // 100 ms

    private readonly string _key, _language, _model;
    private readonly ClientWebSocket _socket = new();
    private readonly Channel<byte[]> _audio = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource<Heard?> _final = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<HeardWord> _words = [];
    private readonly StringBuilder _committed = new();
    private readonly WaveInEvent? _microphone;
    private Task _sending = Task.CompletedTask;
    private volatile bool _committing;
    private string _partial = "";
    private double _level;

    private Ears(string key, string language, string model, bool microphone)
    {
        _key = key;
        _language = language;
        _model = model;
        if (!microphone)
            return;
        _microphone = new WaveInEvent { WaveFormat = new WaveFormat(SampleRate, 16, 1), BufferMilliseconds = 100, NumberOfBuffers = 4 };
        _microphone.DataAvailable += (_, e) =>
        {
            var chunk = e.Buffer.AsSpan(0, e.BytesRecorded).ToArray();
            Measure(chunk);
            _audio.Writer.TryWrite(chunk);
        };
        _microphone.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null)
                Problem ??= "microphone: " + e.Exception.Message;
            _audio.Writer.TryComplete();
        };
    }

    /// <summary>Partial text of the whole utterance so far, as it grows. Raised on a background thread.</summary>
    public event Action<string>? Partial;

    /// <summary>How loud the voice is right now, 0 to 1.</summary>
    public double Level => Volatile.Read(ref _level);

    /// <summary>What went wrong, if anything: no connection, a refusal, no final text in time.</summary>
    public string? Problem { get; private set; }

    /// <summary>For the log: how much sound went out, how loud it was at most, and which answers came when (no text).</summary>
    public string Trace
    {
        get
        {
            lock (_trace)
                return $"{_sentBytes / (SampleRate * 2.0):0.0} s of sound, loudest {_loudest:0.00}; " + string.Join(", ", _trace);
        }
    }

    private readonly List<string> _trace = [];
    private readonly System.Diagnostics.Stopwatch _since = System.Diagnostics.Stopwatch.StartNew();
    private long _sentBytes;
    private double _loudest;

    private void Note(string what)
    {
        lock (_trace)
            _trace.Add($"{what}@{_since.Elapsed.TotalSeconds:0.00}");
    }

    /// <summary>The API key: the process's own, else the user's variable.</summary>
    public static string? Key()
        => Environment.GetEnvironmentVariable(KeyVariable) is { Length: > 0 } own ? own
            : OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable(KeyVariable, EnvironmentVariableTarget.User) : null;

    /// <summary>Listens to the default microphone from now on.</summary>
    public static Ears Listen(string key, string language = "de", string model = "scribe_v3_live")
    {
        var ears = new Ears(key, language, model, microphone: true);
        ears._microphone!.StartRecording();
        ears._sending = Task.Run(ears.SendAsync);
        return ears;
    }

    /// <summary>For a check without a microphone: the given sound (16 kHz, 16-bit, mono) at the pace it would be spoken.</summary>
    public static Ears Hear(string key, byte[] pcm, string language = "de", string model = "scribe_v3_live")
    {
        var ears = new Ears(key, language, model, microphone: false);
        _ = Task.Run(async () =>
        {
            for (var offset = 0; offset < pcm.Length; offset += ChunkBytes)
            {
                var chunk = pcm.AsSpan(offset, Math.Min(ChunkBytes, pcm.Length - offset)).ToArray();
                ears.Measure(chunk);
                await ears._audio.Writer.WriteAsync(chunk);
                await Task.Delay(100);
            }
            ears._audio.Writer.TryComplete(); // the sound is all there is: commit
        });
        ears._sending = Task.Run(ears.SendAsync);
        return ears;
    }

    /// <summary>Stops listening and waits for the final text; what was heard so far when it does not come in time.</summary>
    public async Task<Heard?> FinishAsync(TimeSpan timeout)
    {
        _microphone?.StopRecording(); // the last buffer arrives, then the channel completes and the commit goes out
        try
        {
            return await _final.Task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Problem ??= $"no final text within {timeout.TotalSeconds:0} s";
            return Snapshot(final: false);
        }
    }

    private async Task SendAsync()
    {
        try
        {
            _socket.Options.SetRequestHeader("xi-api-key", _key);
            var url = $"wss://api.elevenlabs.io/v1/speech-to-text/realtime?model_id={_model}&audio_format=pcm_{SampleRate}" +
                      $"&commit_strategy=manual&include_timestamps=true&language_code={_language}";
            await _socket.ConnectAsync(new Uri(url), _stop.Token).ConfigureAwait(false);
            Note("connected");
            _ = Task.Run(ReceiveAsync);
            await foreach (var chunk in _audio.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                await SendAsync(chunk, commit: false).ConfigureAwait(false);
                _sentBytes += chunk.Length;
            }
            _committing = true;
            await SendAsync([], commit: true).ConfigureAwait(false);
            Note("commit");
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or IOException or InvalidOperationException)
        {
            Problem ??= "connection: " + exception.Message;
            _final.TrySetResult(Snapshot(final: false));
        }
    }

    private Task SendAsync(byte[] chunk, bool commit)
    {
        var message = new JsonObject
        {
            ["message_type"] = "input_audio_chunk",
            ["audio_base_64"] = Convert.ToBase64String(chunk),
            ["commit"] = commit,
            ["sample_rate"] = SampleRate,
        };
        return _socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, _stop.Token);
    }

    private async Task ReceiveAsync()
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                var received = await _socket.ReceiveAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                    break;
                message.Write(buffer, 0, received.Count);
                if (!received.EndOfMessage)
                    continue;
                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                if (JsonNode.Parse(text) is JsonObject json)
                    Handle(json);
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or IOException or System.Text.Json.JsonException)
        {
            Note("receive ended: " + exception.GetType().Name);
        }
        finally
        {
            Note($"closed({_socket.CloseStatus})");
            _final.TrySetResult(Snapshot(final: _committing));
        }
    }

    private void Handle(JsonObject message)
    {
        var type = (string?)message["message_type"];
        if (type != "partial_transcript")
            Note(type == "committed_transcript_with_timestamps" ? $"timestamps({(message["words"] as JsonArray)?.Count ?? 0})" : type ?? "?");
        switch (type)
        {
            case "session_started":
                break;
            case "partial_transcript":
                _partial = (string?)message["text"] ?? "";
                Partial?.Invoke(Join(_committed.ToString(), _partial));
                break;
            case "committed_transcript":
                _committed.Append(_committed.Length > 0 ? " " : "").Append(((string?)message["text"] ?? "").Trim());
                _partial = "";
                Partial?.Invoke(_committed.ToString());
                break;
            case "committed_transcript_with_timestamps":
                foreach (var word in (message["words"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    if ((string?)word["type"] is not (null or "word"))
                        continue;
                    _words.Add(new HeardWord(((string?)word["text"] ?? "").Trim(), Number(word["start"]), Number(word["end"])));
                }
                if (_committing)
                    _final.TrySetResult(Snapshot(final: true));
                break;
            default:
                if (message["error"] is { } error)
                {
                    Problem ??= $"{type}: {error}";
                    if (_committing || type is "auth_error" or "quota_exceeded" or "unaccepted_terms" or "invalid_request" or "input_error")
                        _final.TrySetResult(Snapshot(final: false));
                }
                break;
        }
    }

    private Heard Snapshot(bool final)
    {
        var text = Join(_committed.ToString(), _partial);
        return new Heard(text, [.. _words], final && _words.Count > 0);
    }

    private static string Join(string committed, string partial)
        => committed.Length == 0 ? partial.Trim() : partial.Length == 0 ? committed : committed + " " + partial.Trim();

    private static double Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) ? number : 0;

    // Loudness for the ring: the root mean square of the chunk, made livelier for quiet voices.
    private void Measure(byte[] chunk)
    {
        if (chunk.Length < 2)
            return;
        double sum = 0;
        for (var i = 0; i + 1 < chunk.Length; i += 2)
        {
            var sample = BitConverter.ToInt16(chunk, i) / 32768.0;
            sum += sample * sample;
        }
        var rms = Math.Sqrt(sum / (chunk.Length / 2));
        var level = Math.Clamp(Math.Sqrt(rms) * 1.8, 0, 1);
        Volatile.Write(ref _level, level);
        if (level > _loudest)
            _loudest = level;
    }

    public async ValueTask DisposeAsync()
    {
        try { _microphone?.StopRecording(); } catch (InvalidOperationException) { }
        _microphone?.Dispose();
        _audio.Writer.TryComplete();
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is WebSocketException or TimeoutException or OperationCanceledException) { }
        _stop.Cancel();
        try { await _sending.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (Exception) { }
        _socket.Dispose();
        _stop.Dispose();
    }
}
