using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Neo.ExcelMcp.Bridge;

/// <summary>
/// Thin client for the add-in's named pipe. Opens a fresh connection per
/// call — simple and robust for the test phase. The add-in only accepts
/// one client at a time, so we hold a semaphore to serialize calls.
///
/// Wire format (must match PipeServer.cs in the add-in):
///   [4-byte little-endian length][UTF-8 JSON body]
///
/// Request body:  { "method": "&lt;name&gt;", "params": { ... } }
/// Response body: { "result": &lt;any&gt; }  or  { "error": "&lt;message&gt;" }
/// </summary>
public sealed class PipeClient : IDisposable
{
    public const string PipeName = "neo-excel-test";

    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Calls a method on the add-in and returns the raw JSON body of the response.
    /// Throws InvalidOperationException if Excel isn't running (pipe not available).
    /// </summary>
    public async Task<string> CallAsync(string method, object parameters, int connectTimeoutMs = 2000)
    {
        await _lock.WaitAsync();
        try
        {
            using var client = new NamedPipeClientStream(
                ".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            try
            {
                await client.ConnectAsync(connectTimeoutMs);
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException(
                    $@"Could not connect to pipe '\\.\pipe\{PipeName}' within {connectTimeoutMs}ms. " +
                    "Is Excel running with the 'Neo for Excel' add-in loaded?");
            }

            var request = JsonSerializer.Serialize(new { method, @params = parameters });
            await WriteMessageAsync(client, request);

            var response = await ReadMessageAsync(client);
            return response ?? "{}";
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task<string?> ReadMessageAsync(Stream s)
    {
        var lengthBytes = new byte[4];
        int read = 0;
        while (read < 4)
        {
            int n = await s.ReadAsync(lengthBytes.AsMemory(read, 4 - read));
            if (n == 0) return null;
            read += n;
        }

        int length = BitConverter.ToInt32(lengthBytes, 0);
        if (length <= 0 || length > 10 * 1024 * 1024)
            throw new InvalidDataException($"Bad message length: {length}");

        var buf = new byte[length];
        read = 0;
        while (read < length)
        {
            int n = await s.ReadAsync(buf.AsMemory(read, length - read));
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }

        return Encoding.UTF8.GetString(buf);
    }

    private static async Task WriteMessageAsync(Stream s, string message)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var length = BitConverter.GetBytes(body.Length);
        await s.WriteAsync(length);
        await s.WriteAsync(body);
        await s.FlushAsync();
    }

    public void Dispose() => _lock.Dispose();
}
