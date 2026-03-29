using System.Text.Json;

using System.Buffers.Binary;
using System.Text;
using System.IO.Pipes;

using System.Collections.Concurrent;
using System.Buffers;

namespace Neo.IPC
{
    public enum FrameType : ushort
    {
        ControlJson = 1, // IpcEnvelope als JSON
        BlobStart = 2, // Start eines großen Transfers (Metadaten)
        BlobChunk = 3, // Daten-Chunk
        BlobEnd = 4  // Abschluss
    }

    public record BlobStartMeta(
        string Name,           // z.B. "MyPlugin.dll"
        long? Length,         // Gesamtlänge, wenn bekannt (optional)
        string? Sha256 = null, // optionaler Hex-Hash zur Integritätsprüfung
        bool Compressed = false // optional: zukünftige Kompression
    );

    public record IpcEnvelope(
            string Type,            // z.B. "LoadControl", "Ack", "Error"
            string CorrelationId,   // zur Zuordnung von Antworten
            string PayloadJson      // JSON des eigentlichen Payloads
        );

    public static class IpcTypes
    {
        public const string Hello = "Hello";
        public const string Ack = "Ack";
        public const string Error = "Error";
        public const string LoadControl = "LoadControl";
        public const string Log = "Log";
        public const string Heartbeat = "Heartbeat";
        public const string AllowedFolders = "AllowedFolders";
        public const string CursorVisible = "CursorVisible";
        public const string ChildActivated = "ChildActivated";
        public const string SetChildModal = "SetChildModal";
        public const string NotifyFirstChildVisibility = "NotifyFirstChildVisibility";
        public const string SetDesignerMode = "SetDesignerMode";
        public const string DesignerSelection = "DesignerSelection";
        public const string UnloadControl = "UnloadControl";
        public const string ParentWindowBounds = "ParentWindowBounds";
        public const string ToggleChildFullScreen = "ToggleChildFullScreen";
        public const string CaptureScreenshot = "CaptureScreenshot";
        public const string ScreenshotResult = "ScreenshotResult";
        public const string SetProperty = "SetProperty";
        public const string SetPropertyResult = "SetPropertyResult";
        public const string InspectVisualTree = "InspectVisualTree";
        public const string InspectVisualTreeResult = "InspectVisualTreeResult";
        public const string InjectData = "InjectData";
        public const string InjectDataResult = "InjectDataResult";
        public const string ReadData = "ReadData";
        public const string ReadDataResult = "ReadDataResult";
    }

    public record ScreenshotResultMessage(string Base64Png, int Width, int Height);

    /// <summary>
    /// Sets a property on a control in the running app without recompilation.
    /// Target can be: control type ("TextBlock"), Name ("myButton"), or index ("TextBlock:0").
    /// </summary>
    public record SetPropertyRequest(
        string Target,        // e.g. "TextBlock", "Button:2", or a Name/Tag
        string PropertyName,  // e.g. "Foreground", "FontSize", "Text", "IsVisible"
        string Value          // e.g. "Red", "#FF0000", "24", "Hello", "true"
    );

    public record SetPropertyResultMessage(
        bool Success,
        string Message,
        string? OldValue = null,
        string? NewValue = null
    );

    /// <summary>Injects data into items controls or fills form controls.</summary>
    public record InjectDataRequest(
        string Target,                     // Control target: Name, Type, Type:Index, or "root"
        string Mode,                       // "replace", "append", "fill"
        string DataJson,                   // JSON array (replace/append) or JSON object (fill)
        bool AutoTemplate = true,          // Auto-generate ItemTemplate if control has none
        string? FocusFields = null         // Comma-separated field names for auto-template
    );

    public record InjectDataResult(
        bool Success,
        string Message,
        int? ItemCount = null,
        string[]? DetectedFields = null
    );

    /// <summary>Reads data back from items controls or form controls.</summary>
    public record ReadDataRequest(
        string Target,                     // Control target
        string? Scope = null               // "items", "form", "value", or null (auto-detect)
    );

    public record ReadDataResult(
        bool Success,
        string Message,
        string? DataJson = null
    );

    public enum LogLevel
    {
        Info,
        Warn,
        Error
    }

    // Fehler mit vollständiger Exception + Level
    public record ErrorMessage(
        string Message,
        string? ExceptionType = null,
        string? StackTrace = null,
        string? Context = null,
        LogLevel Level = LogLevel.Error
    );

    // Allgemeine Log-Nachricht
    public record LogMessage(
        LogLevel Level,
        string Message,
        string? Category = null,
        string? Details = null
    );

    public record HelloMessage(string Role, int ProcessId, long? Hwnd = null);

    public record LoadControlRequest(
        string AssemblyPath,    // absolut/relativ zum Child-EXE-Verzeichnis
        string TypeName,        // Vollqualifizierter Typname des UserControls
        List<string> NuGetDlls, // Nuget DLLs zum laden!
        string? InitJson = null // optional: Initialisierungsdaten als JSON
    );

    public record AckMessage(string Message);

    public record AllowedFoldersMessage(List<string> Paths, bool ReadWrite);

    public record IsCursorVisible(int isCursorVisible);

    public record IsChildModal(int isModal);

    public record IAmActivated(int unused);

    public record NotifyFirstChildVisibility(int unused);

    public record SetDesignerModeMessage(bool Enabled);

    /// <summary>Parent window screen bounds, sent to child for magnetic docking.</summary>
    public record ParentWindowBoundsMessage(double X, double Y, double Width, double Height, bool IsVisible);

    public record DesignerSelectionMessage(
        string DesignId,
        string ControlType,
        Dictionary<string, string>? Properties = null
    );

    public static class Json
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        public static string ToJson<T>(T obj) => JsonSerializer.Serialize(obj, Options);
        public static T? FromJson<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
    }

    public class PipeMessenger
    {
        private readonly Stream _stream;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public PipeMessenger(Stream stream) => _stream = stream;

        public async Task SendAsync(IpcEnvelope env, CancellationToken ct = default)
        {
            // Serialisieren
            string json = JsonSerializer.Serialize(env, Json.Options);
            byte[] payload = Encoding.UTF8.GetBytes(json);

            // 4-Byte Header (Little Endian)
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);

            // Schreiben strikt serialisieren
            await _sendLock.WaitAsync(ct);
            try
            {
                if (!IsConnected(_stream))
                    throw new IOException("Pipe is not connected.");

                // klassische byte[]-Overloads verwenden
                await _stream.WriteAsync(header, 0, header.Length, ct);
                await _stream.WriteAsync(payload, 0, payload.Length, ct);

                // Flush bei Named Pipes i. d. R. unnötig
                // await _stream.FlushAsync(ct);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        // In PipeMessenger.cs

        public async Task<IpcEnvelope?> ReceiveAsync(CancellationToken ct = default)
        {
            byte[] header = new byte[4];

            // Wir lesen den Header. Wenn der Stream hier endet, ist das okay (sauberer disconnect).
            int headerBytesRead = 0;
            while (headerBytesRead < header.Length)
            {
                int n = await _stream.ReadAsync(header, headerBytesRead, header.Length - headerBytesRead, ct);
                if (n == 0) return null; // Sauberes Ende, bevor eine Nachricht begann.
                headerBytesRead += n;
            }

            int len = BinaryPrimitives.ReadInt32LittleEndian(header);

            // Eine Nachricht der Länge 0 ist kein Fehler, aber ungewöhnlich.
            // Wir behandeln es als "keine Nachricht empfangen" und kehren zurück, um auf die nächste zu warten.
            // Besser ist es, dies als Protokollfehler zu werten, wenn es nicht erwartet wird.
            // Fürs Erste: Behandeln wir es als Fehler, wenn es nicht explizit erlaubt ist.
            if (len <= 0)
            {
                throw new IOException($"Invalid message length received: {len}");
            }

            byte[] payload = new byte[len];

            // Jetzt lesen wir den Payload. Wenn der Stream HIER endet, ist das ein Fehler.
            await ReadExactOrThrowAsync(payload, ct);

            string json = Encoding.UTF8.GetString(payload);
            return JsonSerializer.Deserialize<IpcEnvelope>(json, Json.Options);
        }

        // Ersetzen Sie ReadExactAsync durch diese neue Version, die eine Exception wirft.
        private async Task ReadExactOrThrowAsync(byte[] buffer, CancellationToken ct)
        {
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int bytesRead = await _stream.ReadAsync(buffer, totalRead, buffer.Length - totalRead, ct);
                if (bytesRead == 0)
                {
                    // Unerwartetes Ende des Streams mitten in einer Nachricht!
                    throw new EndOfStreamException("The pipe was closed unexpectedly while reading a message payload.");
                }
                totalRead += bytesRead;
            }
        }

        private static bool IsConnected(Stream s) =>
            s is NamedPipeServerStream srv ? srv.IsConnected :
            s is NamedPipeClientStream cli ? cli.IsConnected : true;
    }

    /// <summary>
    /// Verwalten ausstehender Requests per CorrelationId.
    /// </summary>
    public sealed class PendingRequests
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcEnvelope>> _map
            = new(StringComparer.OrdinalIgnoreCase);

        public Task<IpcEnvelope> Register(string correlationId, TimeSpan timeout, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<IpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_map.TryAdd(correlationId, tcs))
                throw new InvalidOperationException($"Duplicate correlation id '{correlationId}'.");

            // Timeout / Cancel
            if (timeout != Timeout.InfiniteTimeSpan || ct.CanBeCanceled)
            {
                var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (timeout != Timeout.InfiniteTimeSpan)
                    linked.CancelAfter(timeout);

                linked.Token.Register(() =>
                {
                    if (_map.TryRemove(correlationId, out var t))
                        t.TrySetException(new TimeoutException($"Request {correlationId} timed out."));
                });
            }

            return tcs.Task;
        }

        public bool TryComplete(IpcEnvelope response)
        {
            if (string.IsNullOrEmpty(response.CorrelationId)) return false;
            if (_map.TryRemove(response.CorrelationId, out var tcs))
            {
                if (response.Type == IpcTypes.Error)
                {
                    var err = Json.FromJson<ErrorMessage>(response.PayloadJson);
                    tcs.TrySetException(new Exception(
                        $"{err?.Message}\n{err?.ExceptionType}\n{err?.StackTrace}"));
                }
                else
                {
                    tcs.TrySetResult(response);
                }
                return true;
            }
            return false;
        }

        public bool TryCancel(string correlationId, Exception? ex = null)
        {
            if (_map.TryRemove(correlationId, out var tcs))
            {
                if (ex == null) tcs.TrySetCanceled();
                else tcs.TrySetException(ex);
                return true;
            }
            return false;
        }

        public void FailAll(Exception ex)
        {
            foreach (var kv in _map)
                kv.Value.TrySetException(ex);
            _map.Clear();
        }
    }

    /// <summary>
    /// Chunk-basiertes, framing-sicheres Protokoll über einen Stream (Named Pipe).
    /// Diese Version verwendet ausschließlich array-basierte Stream-Overloads,
    /// damit .NET Framework /.NET Standard 2.0 kompatibel bleibt.
    /// </summary>
    public sealed class FramedPipeMessenger
    {
        private readonly Stream _stream;
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        private const uint MAGIC = 0x50495045; // "PIPE"
        private const ushort VERSION = 1;
        private const int HEADER_SIZE = 32;     // 4+2+2+16+8

        public FramedPipeMessenger(Stream stream) => _stream = stream;

        // ---------------------------
        // Public API – CONTROL (JSON)
        // ---------------------------

        public async Task SendControlAsync(IpcEnvelope env, CancellationToken ct = default)
        {
            string json = Json.ToJson(env);
            byte[] utf8 = Encoding.UTF8.GetBytes(json);
            await WriteFrameAsync(FrameType.ControlJson, GuidOrEmpty(env.CorrelationId), utf8, 0, utf8.Length, ct)
                .ConfigureAwait(false);
        }

        public async Task<IpcEnvelope?> ReceiveControlAsync(CancellationToken ct = default)
        {
            while (true)
            {
                var f = await ReadFrameAsync(ct).ConfigureAwait(false);
                if (f == null) return null; // sauber geschlossen

                try
                {
                    if (f.Value.Type == FrameType.ControlJson)
                    {
                        string json = Encoding.UTF8.GetString(f.Value.Buffer, 0, f.Value.Length);
                        return Json.FromJson<IpcEnvelope>(json);
                    }

                    // Andere Frame-Typen ignorieren; BLOB-Receiver läuft separat über ReceiveLoopAsync
                }
                finally
                {
                    f.Value.ReturnIfPooled();
                }
            }
        }

        // ---------------------------------
        // Public API – BLOB Senden/Empfang
        // ---------------------------------

        public async Task SendBlobAsync(
            Guid correlationId,
            BlobStartMeta meta,
            Func<Memory<byte>, CancellationToken, ValueTask<int>> read,
            int chunkSize = 512 * 1024,
            CancellationToken ct = default)
        {
            // Meta senden
            var metaJson = Encoding.UTF8.GetBytes(Json.ToJson(meta));
            await WriteFrameAsync(FrameType.BlobStart, correlationId, metaJson, 0, metaJson.Length, ct)
                .ConfigureAwait(false);

            // Daten streamen
            byte[] buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
            try
            {
                while (true)
                {
                    // read schreibt in buffer[0..n)
                    int n = await read(buffer.AsMemory(0, chunkSize), ct).ConfigureAwait(false);
                    if (n <= 0) break;

                    await WriteFrameAsync(FrameType.BlobChunk, correlationId, buffer, 0, n, ct)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            // Abschluss
            await WriteFrameAsync(FrameType.BlobEnd, correlationId, Array.Empty<byte>(), 0, 0, ct)
                .ConfigureAwait(false);
        }

        public async Task ReceiveLoopAsync(
            Func<IpcEnvelope, Task> onControl,
            Func<Guid, BlobStartMeta, Task> onBlobStart,
            Func<Guid, ReadOnlyMemory<byte>, Task> onBlobChunk,
            Func<Guid, Task> onBlobEnd,
            CancellationToken ct = default)
        {
            while (true)
            {
                var f = await ReadFrameAsync(ct).ConfigureAwait(false);
                if (f == null) return;

                try
                {
                    switch (f.Value.Type)
                    {
                        case FrameType.ControlJson:
                            {
                                string json = Encoding.UTF8.GetString(f.Value.Buffer, 0, f.Value.Length);
                                var env = Json.FromJson<IpcEnvelope>(json)!;
                                await onControl(env);
                                break;
                            }
                        case FrameType.BlobStart:
                            {
                                string json = Encoding.UTF8.GetString(f.Value.Buffer, 0, f.Value.Length);
                                var meta = Json.FromJson<BlobStartMeta>(json)!;
                                await onBlobStart(f.Value.CorrelationId, meta);
                                break;
                            }
                        case FrameType.BlobChunk:
                            {
                                // WICHTIG: Callback synchron abwarten, danach wird Puffer zurückgegeben.
                                await onBlobChunk(f.Value.CorrelationId,
                                    new ReadOnlyMemory<byte>(f.Value.Buffer, 0, f.Value.Length));
                                break;
                            }
                        case FrameType.BlobEnd:
                            {
                                await onBlobEnd(f.Value.CorrelationId);
                                break;
                            }
                        default:
                            // ignorieren
                            break;
                    }
                }
                finally
                {
                    // Gepoolte Payload-Arrays zurückgeben
                    f.Value.ReturnIfPooled();
                }
            }
        }

        // ---------------------------
        // Interne Frame-Helpers
        // ---------------------------

        private async Task WriteFrameAsync(
            FrameType type, Guid corrId, byte[] payload, int offset, int count, CancellationToken ct)
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Header als byte[] (Arrays sind für .NET Fx/Std 2.0 kompatibel)
                byte[] header = new byte[HEADER_SIZE];
                var span = header.AsSpan();

                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0, 4), MAGIC);
                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(4, 2), VERSION);
                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(6, 2), (ushort)type);

                // Guid in Header kopieren (Bytes 8..23)
                var gid = corrId.ToByteArray();
                Buffer.BlockCopy(gid, 0, header, 8, 16);

                BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(24, 8), (ulong)count);

                // Schreiben
                await _stream.WriteAsync(header, 0, header.Length, ct).ConfigureAwait(false);
                if (count > 0)
                    await _stream.WriteAsync(payload, offset, count, ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task<Frame?> ReadFrameAsync(CancellationToken ct)
        {
            byte[] header = new byte[HEADER_SIZE];
            await ReadExactAsync(_stream, header, 0, HEADER_SIZE, ct).ConfigureAwait(false);

            var hspan = header.AsSpan();
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(hspan.Slice(0, 4));
            if (magic == 0 && _stream is { CanRead: true })
            {
                // Manche Streams geben 0er-Header im EOF-Fall – interpretiere als sauber zu.
                return null;
            }
            if (magic != MAGIC) throw new InvalidDataException("Bad frame magic.");

            ushort version = BinaryPrimitives.ReadUInt16LittleEndian(hspan.Slice(4, 2));
            if (version != VERSION) throw new InvalidDataException($"Unsupported frame version {version}.");

            FrameType type = (FrameType)BinaryPrimitives.ReadUInt16LittleEndian(hspan.Slice(6, 2));

            byte[] gid = new byte[16];
            Buffer.BlockCopy(header, 8, gid, 0, 16);
            var corrId = new Guid(gid);

            ulong len64 = BinaryPrimitives.ReadUInt64LittleEndian(hspan.Slice(24, 8));
            if (len64 > int.MaxValue) throw new InvalidDataException("Payload too large for single frame.");
            int len = (int)len64;

            byte[] payload;
            bool pooled = false;

            if (len > 0)
            {
                payload = ArrayPool<byte>.Shared.Rent(len);
                pooled = true;
                await ReadExactAsync(_stream, payload, 0, len, ct).ConfigureAwait(false);
            }
            else
            {
                payload = Array.Empty<byte>();
            }

            return new Frame(type, corrId, payload, len, pooled);
        }

        private static async Task ReadExactAsync(Stream s, byte[] buf, int off, int len, CancellationToken ct)
        {
            int total = 0;
            while (total < len)
            {
                int n = await s.ReadAsync(buf, off + total, len - total, ct).ConfigureAwait(false);
                if (n == 0) throw new EndOfStreamException("Unexpected EOF during payload.");
                total += n;
            }
        }

        private static Guid GuidOrEmpty(string? s)
            => Guid.TryParse(s, out var g) ? g : Guid.Empty;

        private readonly struct Frame
        {
            public FrameType Type { get; }
            public Guid CorrelationId { get; }
            public byte[] Buffer { get; }
            public int Length { get; }
            private readonly bool _pooled;

            public Frame(FrameType type, Guid correlationId, byte[] buffer, int length, bool pooled)
            {
                Type = type;
                CorrelationId = correlationId;
                Buffer = buffer;
                Length = length;
                _pooled = pooled;
            }

            public void ReturnIfPooled()
            {
                if (_pooled && Buffer != null && Buffer.Length > 0)
                {
                    ArrayPool<byte>.Shared.Return(Buffer);
                }
            }
        }
    }
}
