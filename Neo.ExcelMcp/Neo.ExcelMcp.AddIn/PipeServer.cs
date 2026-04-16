using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Neo.ExcelMcp.AddIn;

/// <summary>
/// Tiny JSON-RPC-ish named pipe server. One client at a time.
/// Wire format: [4-byte little-endian length][UTF-8 JSON body].
///
/// Requests:  { "method": "&lt;name&gt;", "params": { ... } }
/// Responses: { "result": &lt;any&gt; }  or  { "error": "&lt;message&gt;" }
///
/// Runs on the thread pool. Every Excel COM access MUST go through
/// ExcelGateway.RunOnMainAsync — never touch COM from the accept loop.
/// </summary>
internal sealed class PipeServer
{
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public PipeServer(string pipeName)
    {
        _pipeName = pipeName;
    }

    public void Start()
    {
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        try { _cts.Cancel(); } catch { /* ignore */ }
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(3)); } catch { /* ignore */ }
        try { _cts.Dispose(); } catch { /* ignore */ }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                Log.Info("Waiting for client connection...");
                await server.WaitForConnectionAsync(ct);
                Log.Info("Client connected");

                await HandleClientAsync(server, ct);
                Log.Info("Client disconnected");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error($"AcceptLoop error: {ex.Message}");
                try { await Task.Delay(500, ct); } catch { break; }
            }
            finally
            {
                try { server?.Dispose(); } catch { /* ignore */ }
            }
        }
        Log.Info("AcceptLoop exited");
    }

    private static async Task HandleClientAsync(NamedPipeServerStream stream, CancellationToken ct)
    {
        while (stream.IsConnected && !ct.IsCancellationRequested)
        {
            string? request;
            try
            {
                request = await ReadMessageAsync(stream, ct);
            }
            catch (EndOfStreamException) { return; }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Error($"Read failed: {ex.Message}");
                return;
            }

            if (request == null) return;
            Log.Info($"<- {request}");

            string response;
            try
            {
                response = await HandleMessageAsync(request);
            }
            catch (Exception ex)
            {
                response = JsonSerializer.Serialize(new { error = ex.Message });
            }

            Log.Info($"-> {response}");

            try
            {
                await WriteMessageAsync(stream, response, ct);
            }
            catch (Exception ex)
            {
                Log.Error($"Write failed: {ex.Message}");
                return;
            }
        }
    }

    private static async Task<string?> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        var lengthBytes = new byte[4];
        int read = 0;
        while (read < 4)
        {
            int n = await stream.ReadAsync(lengthBytes.AsMemory(read, 4 - read), ct);
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
            int n = await stream.ReadAsync(buf.AsMemory(read, length - read), ct);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }

        return Encoding.UTF8.GetString(buf);
    }

    private static async Task WriteMessageAsync(Stream stream, string message, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var length = BitConverter.GetBytes(body.Length);
        await stream.WriteAsync(length, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<string> HandleMessageAsync(string json)
    {
        string method;
        JsonElement paramsEl = default;
        try
        {
            using var doc = JsonDocument.Parse(json);
            method = doc.RootElement.GetProperty("method").GetString() ?? "";
            if (doc.RootElement.TryGetProperty("params", out var p))
                paramsEl = p.Clone();
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"Invalid JSON: {ex.Message}" });
        }

        switch (method)
        {
            case "ping":
                return JsonSerializer.Serialize(new { result = new { pong = true } });

            case "excel_context":
                try
                {
                    var ctx = await ExcelGateway.GetContextAsync();
                    return JsonSerializer.Serialize(new { result = ctx });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { error = ex.Message });
                }

            case "excel_read":
                try
                {
                    string? sheet = null, range = null;
                    if (paramsEl.ValueKind == JsonValueKind.Object)
                    {
                        if (paramsEl.TryGetProperty("sheet", out var s)) sheet = s.GetString();
                        if (paramsEl.TryGetProperty("range", out var r)) range = r.GetString();
                    }
                    var data = await ExcelGateway.ReadRangeAsync(sheet, range);
                    return JsonSerializer.Serialize(new { result = data });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { error = ex.Message });
                }

            case "excel_write":
                try
                {
                    string? wSheet = null, wRange = null;
                    List<List<object?>>? wValues = null;
                    if (paramsEl.ValueKind == JsonValueKind.Object)
                    {
                        if (paramsEl.TryGetProperty("sheet", out var ws)) wSheet = ws.GetString();
                        if (paramsEl.TryGetProperty("range", out var wr)) wRange = wr.GetString();
                        if (paramsEl.TryGetProperty("values", out var wv) && wv.ValueKind == JsonValueKind.Array)
                            wValues = ParseValuesArray(wv);
                    }
                    if (wValues == null)
                        return JsonSerializer.Serialize(new { error = "Missing 'values' parameter (2D array required)." });
                    var writeResult = await ExcelGateway.WriteRangeAsync(wSheet, wRange!, wValues);
                    return JsonSerializer.Serialize(new { result = writeResult });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { error = ex.Message });
                }

            case "excel_tables":
                try
                {
                    var tables = await ExcelGateway.ListTablesAsync();
                    return JsonSerializer.Serialize(new { result = tables });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { error = ex.Message });
                }

            case "excel_write_formula":
                try
                {
                    string? fSheet = null, fRange = null;
                    List<List<object?>>? fValues = null;
                    if (paramsEl.ValueKind == JsonValueKind.Object)
                    {
                        if (paramsEl.TryGetProperty("sheet", out var fs)) fSheet = fs.GetString();
                        if (paramsEl.TryGetProperty("range", out var fr)) fRange = fr.GetString();
                        if (paramsEl.TryGetProperty("formulas", out var fv) && fv.ValueKind == JsonValueKind.Array)
                            fValues = ParseValuesArray(fv);
                    }
                    if (fValues == null)
                        return JsonSerializer.Serialize(new { error = "Missing 'formulas' parameter." });
                    var fResult = await ExcelGateway.WriteFormulaAsync(fSheet, fRange!, fValues);
                    return JsonSerializer.Serialize(new { result = fResult });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { error = ex.Message });
                }

            case "excel_read_table":
                try
                {
                    string? tableName = null;
                    if (paramsEl.ValueKind == JsonValueKind.Object)
                        if (paramsEl.TryGetProperty("name", out var tn)) tableName = tn.GetString();
                    if (string.IsNullOrWhiteSpace(tableName))
                        return JsonSerializer.Serialize(new { error = "Missing 'name' parameter." });
                    var tableData = await ExcelGateway.ReadTableAsync(tableName!);
                    return JsonSerializer.Serialize(new { result = tableData });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { error = ex.Message });
                }

            case "excel_create_sheet":
                try
                {
                    string? sheetName = null;
                    if (paramsEl.ValueKind == JsonValueKind.Object)
                        if (paramsEl.TryGetProperty("name", out var sn)) sheetName = sn.GetString();
                    if (string.IsNullOrWhiteSpace(sheetName))
                        return JsonSerializer.Serialize(new { error = "Missing 'name' parameter." });
                    var csResult = await ExcelGateway.CreateSheetAsync(sheetName!);
                    return JsonSerializer.Serialize(new { result = csResult });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { error = ex.Message });
                }

            case "excel_format":
                try
                {
                    string? fmtSheet = null, fmtRange = null, fontColor = null, bgColor = null;
                    string? numFormat = null, hAlign = null;
                    bool? fmtBold = null, fmtItalic = null, fmtBorders = null;
                    double? fmtFontSize = null;
                    if (paramsEl.ValueKind == JsonValueKind.Object)
                    {
                        if (paramsEl.TryGetProperty("sheet", out var s)) fmtSheet = s.GetString();
                        if (paramsEl.TryGetProperty("range", out var r)) fmtRange = r.GetString();
                        if (paramsEl.TryGetProperty("bold", out var b)) fmtBold = b.GetBoolean();
                        if (paramsEl.TryGetProperty("italic", out var it)) fmtItalic = it.GetBoolean();
                        if (paramsEl.TryGetProperty("fontSize", out var fs)) fmtFontSize = fs.GetDouble();
                        if (paramsEl.TryGetProperty("fontColor", out var fc)) fontColor = fc.GetString();
                        if (paramsEl.TryGetProperty("bgColor", out var bg)) bgColor = bg.GetString();
                        if (paramsEl.TryGetProperty("numberFormat", out var nf)) numFormat = nf.GetString();
                        if (paramsEl.TryGetProperty("horizontalAlignment", out var ha)) hAlign = ha.GetString();
                        if (paramsEl.TryGetProperty("borders", out var bo)) fmtBorders = bo.GetBoolean();
                    }
                    var fmtResult = await ExcelGateway.FormatRangeAsync(
                        fmtSheet, fmtRange!, fmtBold, fmtItalic,
                        fmtFontSize, fontColor, bgColor,
                        numFormat, hAlign, fmtBorders);
                    return JsonSerializer.Serialize(new { result = fmtResult });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { error = ex.Message });
                }

            default:
                return JsonSerializer.Serialize(new { error = $"Unknown method: {method}" });
        }
    }

    private static List<List<object?>> ParseValuesArray(JsonElement arrayEl)
    {
        var result = new List<List<object?>>();
        foreach (var row in arrayEl.EnumerateArray())
        {
            var rowList = new List<object?>();
            if (row.ValueKind == JsonValueKind.Array)
            {
                foreach (var cell in row.EnumerateArray())
                {
                    rowList.Add(cell.ValueKind switch
                    {
                        JsonValueKind.Number => cell.TryGetDouble(out var d) ? (object)d : cell.GetRawText(),
                        JsonValueKind.String => cell.GetString(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Null => "",
                        _ => cell.GetRawText()
                    });
                }
            }
            result.Add(rowList);
        }
        return result;
    }
}
