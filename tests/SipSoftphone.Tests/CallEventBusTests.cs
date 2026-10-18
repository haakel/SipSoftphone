using System.Text.Json;
using SipSoftphone;
using Xunit;

namespace SipSoftphone.Tests;

public class CallEventBusTests : IDisposable
{
    private readonly string _dir;
    private readonly string _logPath;

    public CallEventBusTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sipsoftphone-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _logPath = Path.Combine(_dir, "calls.jsonl");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private CallEventBus CreateBus() => new(
        new HookConfig { LogFile = _logPath, WebhookUrl = "", SseEnabled = false, SsePort = 0 },
        sse: null);

    [Fact]
    public void Emit_writes_jsonl_line_with_expected_fields()
    {
        using var bus = CreateBus();
        bus.Emit("call_received", "9121234567", "Ali", "incoming INVITE");

        var lines = File.ReadAllLines(_logPath);
        Assert.Single(lines);

        using var doc = JsonDocument.Parse(lines[0]);
        var r = doc.RootElement;
        Assert.Equal("call_received", r.GetProperty("event").GetString());
        Assert.Equal("9121234567", r.GetProperty("phone").GetString());
        Assert.Equal("Ali", r.GetProperty("name").GetString());
        Assert.Equal("incoming INVITE", r.GetProperty("detail").GetString());
        Assert.True(r.TryGetProperty("ts", out _));
        Assert.Equal("sipsoftphone", r.GetProperty("source").GetString());
    }

    [Fact]
    public void Emit_fires_InProcess_Emitted_event()
    {
        using var bus = CreateBus();
        CallEvent? received = null;
        bus.Emitted += ev => received = ev;

        bus.Emit("call_answered", "956", null, "user answered");

        Assert.NotNull(received);
        Assert.Equal("call_answered", received!.Event);
        Assert.Equal("956", received.Phone);
        Assert.Null(received.Name);
    }

    [Fact]
    public void Emit_appends_multiple_events_in_order()
    {
        using var bus = CreateBus();
        bus.Emit("call_outgoing", "111", null, "a");
        bus.Emit("call_ended", "111", null, "b");

        var lines = File.ReadAllLines(_logPath);
        Assert.Equal(2, lines.Length);
        Assert.Contains("call_outgoing", lines[0]);
        Assert.Contains("call_ended", lines[1]);
    }

    [Fact]
    public void Emit_with_invalid_log_dir_does_not_throw()
    {
        var cfg = new HookConfig { LogFile = Path.Combine(_dir, "no", "such", "dir", "x.jsonl"), WebhookUrl = "" };
        using var bus = new CallEventBus(cfg, null);
        var ex = Record.Exception(() => bus.Emit("call_received", "1", null, "d"));
        Assert.Null(ex);
        Assert.True(File.Exists(Path.Combine(_dir, "no", "such", "dir", "x.jsonl")));
    }
}
