using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SipSoftphone;

/// <summary>
/// Minimal loopback HTTP server that streams events as Server-Sent Events.
///   GET /events  -> text/event-stream (data-only frames, es.onmessage works)
///   GET /health  -> {"ok":true}
///   OPTIONS *    -> CORS preflight (chrome-extension:// origins allowed)
/// Binds 127.0.0.1 only. No admin rights needed (raw TcpListener, not HttpListener).
/// </summary>
public class SseServer : IDisposable
{
    private readonly int _port;
    private readonly TcpListener _listener;
    private readonly List<NetworkStream> _clients = new();
    private readonly object _clientsLock = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private Task? _heartbeat;

    public SseServer(int port)
    {
        _port = port;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
        _heartbeat = Task.Run(HeartbeatAsync);
        Diag.WriteLine($"[sse] listening on http://127.0.0.1:{_port}/events");
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (Exception) { continue; }
            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            using var _ = client;
            client.NoDelay = true;
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);

            var requestLine = await reader.ReadLineAsync(_cts.Token) ?? "";
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_cts.Token))) { }

            var path = requestLine.Split(' ').ElementAtOrDefault(1) ?? "/";

            if (requestLine.StartsWith("OPTIONS"))
            {
                await WriteAscii(stream, "HTTP/1.1 204 No Content\r\n" + CorsHeaders() + "Access-Control-Allow-Methods: GET, OPTIONS\r\nAccess-Control-Allow-Headers: *\r\nContent-Length: 0\r\n\r\n");
                return;
            }

            if (path.StartsWith("/health"))
            {
                var body = "{\"ok\":true,\"port\":" + _port + "}";
                await WriteAscii(stream, "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" + CorsHeaders() + $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n" + body);
                return;
            }

            if (path.StartsWith("/events"))
            {
                await WriteAscii(stream,
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: text/event-stream\r\n" +
                    "Cache-Control: no-cache\r\n" +
                    "Connection: keep-alive\r\n" +
                    CorsHeaders() +
                    "X-Accel-Buffering: no\r\n" +
                    "\r\n" +
                    "retry: 3000\n\n");

                lock (_clientsLock) _clients.Add(stream);
                // keep the connection task alive until client disconnects or server stops
                await Task.Delay(Timeout.Infinite, _cts.Token);
                return;
            }

            await WriteAscii(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        }
        catch (Exception)
        {
            // client gone — nothing to do
        }
        finally
        {
            var st = SafeStream(client);
            if (st != null)
            {
                lock (_clientsLock) _clients.Remove(st);
                try { st.Dispose(); } catch { }
            }
        }
    }

    private static NetworkStream? SafeStream(TcpClient c) => c.Client != null && c.Connected ? (c.GetStream()) : null;

    private async Task HeartbeatAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(15), _cts.Token); }
            catch (OperationCanceledException) { break; }

            List<NetworkStream> snapshot;
            lock (_clientsLock) snapshot = _clients.ToList();

            foreach (var s in snapshot)
            {
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(": ping\n\n");
                    await s.WriteAsync(bytes, _cts.Token);
                    await s.FlushAsync(_cts.Token);
                }
                catch
                {
                    lock (_clientsLock) _clients.Remove(s);
                    try { s.Dispose(); } catch { }
                }
            }
        }
    }

    public void Broadcast(string json)
    {
        var frame = Encoding.UTF8.GetBytes($"data: {json}\n\n");
        List<NetworkStream> snapshot;
        lock (_clientsLock) snapshot = _clients.ToList();

        foreach (var s in snapshot)
        {
            try
            {
                s.Write(frame);
                s.Flush();
            }
            catch
            {
                lock (_clientsLock) _clients.Remove(s);
                try { s.Dispose(); } catch { }
            }
        }
    }

    private static string CorsHeaders() =>
        "Access-Control-Allow-Origin: *\r\n" +
        "Access-Control-Allow-Private-Network: true\r\n";

    private static async Task WriteAscii(NetworkStream s, string text)
    {
        var b = Encoding.ASCII.GetBytes(text);
        await s.WriteAsync(b);
        await s.FlushAsync();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        lock (_clientsLock)
        {
            foreach (var s in _clients) try { s.Dispose(); } catch { }
            _clients.Clear();
        }
        try { _acceptLoop?.Wait(1000); } catch { }
        try { _heartbeat?.Wait(1000); } catch { }
        _cts.Dispose();
    }
}
