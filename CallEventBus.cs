using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace SipSoftphone;

public record CallEvent(string Event, string Phone, string? Name, string Detail, string Ts)
{
    public string ToJson() => JsonSerializer.Serialize(new
    {
        @event = Event,
        phone = Phone,
        name = Name,
        detail = Detail,
        ts = Ts,
        source = "sipsoftphone"
    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
}

public class CallEventBus : IDisposable
{
    private readonly HookConfig _cfg;
    private readonly SseServer? _sse;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly object _fileLock = new();
    private readonly string _logPath;

    public CallEventBus(HookConfig cfg, SseServer? sse)
    {
        _cfg = cfg;
        _sse = sse;
        _logPath = Path.IsPathRooted(cfg.LogFile) ? cfg.LogFile : Path.Combine(AppContext.BaseDirectory, cfg.LogFile);
        if (!string.IsNullOrEmpty(_cfg.LogFile))
        {
            var dir = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
    }

    /// <summary>Fires for every emitted event (after JSONL + SSE + webhook), for in-process consumers (UI history).</summary>
    public event Action<CallEvent>? Emitted;

    public void Emit(string evt, string phone, string? name, string detail)
    {
        var ev = new CallEvent(evt, phone, name, detail, DateTime.UtcNow.ToString("o"));
        try { Emitted?.Invoke(ev); } catch { }
        var json = ev.ToJson();

        // 1) JSONL log file (pollable by any program)
        if (!string.IsNullOrEmpty(_cfg.LogFile))
        {
            try
            {
                lock (_fileLock) File.AppendAllText(_logPath, json + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) { Diag.WriteLine($"[hook] log write failed: {ex.Message}"); }
        }

        // 2) SSE push (real-time, for the browser extension)
        _sse?.Broadcast(json);

        // 3) optional webhook POST
        if (!string.IsNullOrWhiteSpace(_cfg.WebhookUrl))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var c = new StringContent(json, Encoding.UTF8, "application/json");
                    using var resp = await _http.PostAsync(_cfg.WebhookUrl, c);
                    if (!resp.IsSuccessStatusCode)
                        Diag.WriteLine($"[hook] webhook {(int)resp.StatusCode} from {_cfg.WebhookUrl}");
                }
                catch (Exception ex) { Diag.WriteLine($"[hook] webhook failed: {ex.Message}"); }
            });
        }

        Diag.WriteLine($"[event] {evt} phone={phone} {detail}");
    }

    public void Dispose() => _http.Dispose();
}
