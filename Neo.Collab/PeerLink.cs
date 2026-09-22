using System.Buffers.Binary;
using System.IO.Compression;

namespace Neo.Collab;

/// <summary>
/// A reliable, ordered message connection to one other N.E.O.: WebRTC in real sessions
/// (<see cref="WebRtcPeerLink"/>), in memory in tests (<see cref="InMemoryPeerLink"/>).
/// Messages are whole byte arrays of any size.
/// </summary>
public interface IPeerLink : IDisposable
{
    /// <summary>A complete message arrived. Raised on a background thread, in order.</summary>
    event Action<byte[]>? MessageReceived;

    /// <summary>The connection ended; the argument says why. Raised at most once.</summary>
    event Action<string>? Closed;

    bool IsOpen { get; }

    Task SendAsync(byte[] message);

    /// <summary>Ends the connection; the other side sees <see cref="Closed"/>.</summary>
    void Close(string reason);
}

/// <summary>
/// Splits messages into pieces the transport accepts and puts them back together; compresses
/// them on the way (session state is mostly source code and chat text, which shrinks a lot).
/// Piece layout: message id (4 bytes), piece index (2), piece count (2), data.
/// </summary>
internal sealed class MessageFramer
{
    public const int MaxPiece = 60 * 1024; // well below the data channel's 256 KB message limit
    private const int HeaderLength = 8;

    private uint _nextId;
    private readonly Dictionary<uint, (byte[][] Pieces, int Received)> _partial = new();

    public IEnumerable<byte[]> Split(byte[] message)
    {
        var compressed = Compress(message);
        int count = Math.Max(1, (compressed.Length + MaxPiece - 1) / MaxPiece);
        if (count > ushort.MaxValue) throw new InvalidOperationException("Message too large.");
        uint id = unchecked(++_nextId);
        for (int i = 0; i < count; i++)
        {
            int offset = i * MaxPiece;
            int length = Math.Min(MaxPiece, compressed.Length - offset);
            var piece = new byte[HeaderLength + length];
            BinaryPrimitives.WriteUInt32BigEndian(piece, id);
            BinaryPrimitives.WriteUInt16BigEndian(piece.AsSpan(4), (ushort)i);
            BinaryPrimitives.WriteUInt16BigEndian(piece.AsSpan(6), (ushort)count);
            Buffer.BlockCopy(compressed, offset, piece, HeaderLength, length);
            yield return piece;
        }
    }

    /// <summary>Takes a piece; returns the whole message once its last piece arrived.</summary>
    public byte[]? Add(byte[] piece)
    {
        if (piece.Length < HeaderLength) return null;
        uint id = BinaryPrimitives.ReadUInt32BigEndian(piece);
        int index = BinaryPrimitives.ReadUInt16BigEndian(piece.AsSpan(4));
        int count = BinaryPrimitives.ReadUInt16BigEndian(piece.AsSpan(6));
        if (count == 0 || index >= count) return null;

        if (!_partial.TryGetValue(id, out var entry))
            entry = (new byte[count][], 0);
        if (entry.Pieces[index] == null)
        {
            entry.Pieces[index] = piece[HeaderLength..];
            entry.Received++;
        }
        if (entry.Received < count)
        {
            _partial[id] = entry;
            return null;
        }
        _partial.Remove(id);
        var total = new byte[entry.Pieces.Sum(p => p.Length)];
        int at = 0;
        foreach (var p in entry.Pieces) { Buffer.BlockCopy(p, 0, total, at, p.Length); at += p.Length; }
        return Decompress(total);
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
            brotli.Write(data);
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return output.ToArray();
    }
}

/// <summary>Two ends of a connection inside one process — for tests of everything above the transport.</summary>
public sealed class InMemoryPeerLink : IPeerLink
{
    private InMemoryPeerLink? _other;
    private readonly object _gate = new();
    private Task _delivery = Task.CompletedTask;
    private int _closed;

    public event Action<byte[]>? MessageReceived;
    public event Action<string>? Closed;

    public bool IsOpen => Volatile.Read(ref _closed) == 0;

    public static (InMemoryPeerLink A, InMemoryPeerLink B) CreatePair()
    {
        var a = new InMemoryPeerLink();
        var b = new InMemoryPeerLink();
        a._other = b;
        b._other = a;
        return (a, b);
    }

    public Task SendAsync(byte[] message)
    {
        if (!IsOpen || _other == null || !_other.IsOpen) throw new InvalidOperationException("The connection is closed.");
        var copy = (byte[])message.Clone();
        _other.Deliver(copy);
        return Task.CompletedTask;
    }

    // In order, one after the other, never on the sender's stack — like a real connection.
    private void Deliver(byte[] message)
    {
        lock (_gate)
            _delivery = _delivery.ContinueWith(_ => { if (IsOpen) MessageReceived?.Invoke(message); }, TaskScheduler.Default);
    }

    public void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        var other = _other;
        Task.Run(() => Closed?.Invoke(reason));
        if (other != null)
        {
            lock (other._gate)
                other._delivery = other._delivery.ContinueWith(_ => other.Close("The other side left: " + reason), TaskScheduler.Default);
        }
    }

    public void Dispose() => Close("closed");
}
