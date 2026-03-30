using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Neo.PluginWindowAvalonia;

/// <summary>
/// Hosts an HTTP + WebSocket server inside the PluginWindow process.
/// Serves an HTML page and enables bidirectional real-time communication
/// between a web browser and the running Avalonia app.
/// </summary>
internal sealed class WebBridgeServer : IDisposable
{
    private HttpListener? _listener;
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();
    private string _htmlContent = "";
    private CancellationTokenSource _cts = new();
    private Task? _acceptLoopTask;
    private bool _isRunning;

    public int Port { get; private set; }
    public string Url => $"http://localhost:{Port}/";
    public string WsUrl => $"ws://localhost:{Port}/";
    public bool IsRunning => _isRunning;
    public int ClientCount => _clients.Count;

    /// <summary>Fired when a WebSocket message is received from any browser client.</summary>
    public event Action<string>? MessageReceived;

    public bool Start(string htmlContent, int port = 0)
    {
        if (_isRunning) Stop();

        _htmlContent = htmlContent;
        Port = port > 0 ? port : FindAvailablePort();
        _cts = new CancellationTokenSource();

        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            _listener.Start();
            _isRunning = true;

            _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
            Debug.WriteLine($"[WebBridge] Started on http://localhost:{Port}/");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebBridge] Failed to start: {ex.Message}");
            _isRunning = false;
            return false;
        }
    }

    public void Stop()
    {
        _isRunning = false;
        try { _cts.Cancel(); } catch { }

        // Close all WebSocket clients
        foreach (var (id, ws) in _clients)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                    ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server stopping", CancellationToken.None)
                        .Wait(TimeSpan.FromSeconds(2));
            }
            catch { }
            try { ws.Dispose(); } catch { }
        }
        _clients.Clear();

        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _listener = null;

        Debug.WriteLine("[WebBridge] Stopped.");
    }

    /// <summary>Sends a message to all connected WebSocket clients.</summary>
    public async Task SendToAllAsync(string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        var deadClients = new List<Guid>();

        foreach (var (id, ws) in _clients)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
                else
                    deadClients.Add(id);
            }
            catch
            {
                deadClients.Add(id);
            }
        }

        foreach (var id in deadClients)
        {
            if (_clients.TryRemove(id, out var dead))
                try { dead.Dispose(); } catch { }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var context = await _listener.GetContextAsync();

                if (context.Request.IsWebSocketRequest)
                {
                    // WebSocket upgrade
                    _ = Task.Run(() => HandleWebSocketAsync(context, ct));
                }
                else
                {
                    // Serve HTML page
                    ServeHtml(context);
                }
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebBridge] Accept error: {ex.Message}");
            }
        }
    }

    private void ServeHtml(HttpListenerContext context)
    {
        try
        {
            var html = _htmlContent;

            // Auto-inject WebSocket URL if placeholder exists
            html = html.Replace("{{WS_URL}}", WsUrl);
            html = html.Replace("{{PORT}}", Port.ToString());

            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.StatusCode = 200;

            // Allow local CORS
            context.Response.Headers.Add("Access-Control-Allow-Origin", "*");

            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.Close();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebBridge] ServeHtml error: {ex.Message}");
        }
    }

    private async Task HandleWebSocketAsync(HttpListenerContext httpContext, CancellationToken ct)
    {
        WebSocket? ws = null;
        var clientId = Guid.NewGuid();

        try
        {
            var wsContext = await httpContext.AcceptWebSocketAsync(null);
            ws = wsContext.WebSocket;
            _clients[clientId] = ws;
            Debug.WriteLine($"[WebBridge] Client connected: {clientId} (total: {_clients.Count})");

            var buffer = new byte[8192];

            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    Debug.WriteLine($"[WebBridge] Message from browser: {message}");
                    MessageReceived?.Invoke(message);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebBridge] WebSocket error: {ex.Message}");
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
            try { ws?.Dispose(); } catch { }
            Debug.WriteLine($"[WebBridge] Client disconnected: {clientId} (total: {_clients.Count})");
        }
    }

    private static int FindAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        Stop();
    }
}
