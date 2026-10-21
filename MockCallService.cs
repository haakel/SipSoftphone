using System.IO;

namespace SipSoftphone;

/// <summary>
/// Mock call service for UI development / demo mode ("--demo").
/// Simulates registration, outgoing calls, hold, mute and incoming calls —
/// no SIP traffic, no audio, no network. Implements <see cref="ICallService"/>
/// exactly like <see cref="PhoneEngine"/>, so every UI state can be exercised
/// without a backend. History events are written to their own JSONL file.
/// </summary>
public sealed class MockCallService : ICallService
{
    private readonly object _sync = new();
    private readonly CallEventBus _bus;
    private CallSnapshot _call = CallSnapshot.Empty;
    private RegistrationStatus _reg = new(true, false, "Demo offline");

    public MockCallService()
    {
        _bus = new CallEventBus(new HookConfig
        {
            SseEnabled = false,
            SsePort = 0,
            LogFile = Path.Combine(Path.GetTempPath(), "sipsoftphone-demo-history.jsonl"),
            WebhookUrl = ""
        }, null);
    }

    public CallSnapshot CurrentCall { get { lock (_sync) return _call; } }
    public RegistrationStatus Registration { get { lock (_sync) return _reg; } }
    public string ServerInfo => "Demo mode — mock call service (no SIP traffic)";
    public CallEventBus? Bus => _bus;

    public event Action<string>? Log;
    public event Action<CallSnapshot>? CallChanged;
    public event Action<RegistrationStatus>? RegistrationChanged;
    public event Action<string, string?>? IncomingAlert;

    // ---------- lifecycle ----------
    public bool Start()
    {
        RaiseLog("[demo] mock engine started — UI states only, no SIP calls");
        _ = Task.Run(async () =>
        {
            await Task.Delay(600);
            SetReg(new RegistrationStatus(true, true, "Registered (demo)", DateTime.Now.AddMinutes(30)));

            // periodic simulated incoming call so the incoming UI can be inspected
            while (true)
            {
                await Task.Delay(45_000);
                if (CurrentCall.State == CallState.Idle) SimulateIncoming();
            }
        });
        return true;
    }

    public bool Restart()
    {
        RaiseLog("[demo] mock engine restarted");
        SetReg(new RegistrationStatus(true, true, "Registered (demo)", DateTime.Now.AddMinutes(30)));
        return true;
    }

    public void Register()
    {
        SetReg(new RegistrationStatus(true, true, "Registered (demo)", DateTime.Now.AddMinutes(30)));
        _bus?.Emit("registration", "", null, "demo registered");
    }

    public void Unregister()
    {
        SetReg(new RegistrationStatus(true, false, "Unregistered (demo)"));
    }

    /// <summary>Fires a simulated incoming call into the UI (used by the demo loop).</summary>
    public void SimulateIncoming(string number = "09123456789", string? name = "Demo Caller")
    {
        lock (_sync)
        {
            if (_call.State != CallState.Idle) return;
            _call = new CallSnapshot(CallState.IncomingRinging, number, number, name, null, null);
        }
        RaiseLog($"[demo] incoming call from {number}");
        _bus?.Emit("call_received", number, name, "demo incoming");
        CallChanged?.Invoke(CurrentCall);
        IncomingAlert?.Invoke(number, name);
    }

    // ---------- outgoing ----------
    public Task<bool> CallAsync(string number)
    {
        if (string.IsNullOrWhiteSpace(number)) return Task.FromResult(false);
        lock (_sync)
        {
            if (_call.State is not (CallState.Idle or CallState.Ended)) return Task.FromResult(false);
            _call = new CallSnapshot(CallState.OutgoingRinging, number, number, null, null, null);
        }
        RaiseLog($"[demo] dialing {number}");
        _bus?.Emit("call_outgoing", number, null, "demo dial");
        CallChanged?.Invoke(CurrentCall);

        _ = Task.Run(async () =>
        {
            await Task.Delay(2500);
            lock (_sync)
            {
                if (_call.State != CallState.OutgoingRinging) return;   // cancelled / failed meanwhile
                _call = _call with { State = CallState.Active, AnsweredAt = DateTime.Now, Codec = "PCMU (demo)" };
            }
            RaiseLog("[demo] call answered");
            _bus?.Emit("call_answered", number, null, "demo answered");
            CallChanged?.Invoke(CurrentCall);
        });
        return Task.FromResult(true);
    }

    // ---------- incoming ----------
    public Task AnswerPendingAsync()
    {
        lock (_sync)
        {
            if (_call.State != CallState.IncomingRinging) return Task.CompletedTask;
            _call = _call with { State = CallState.Active, AnsweredAt = DateTime.Now, Codec = "PCMU (demo)" };
        }
        RaiseLog("[demo] incoming call answered");
        _bus?.Emit("call_answered", _call.RawNumber, _call.DisplayName, "demo answered");
        CallChanged?.Invoke(CurrentCall);
        return Task.CompletedTask;
    }

    public void RejectPending()
    {
        string phone; string? name;
        lock (_sync)
        {
            if (_call.State != CallState.IncomingRinging) return;
            phone = _call.RawNumber; name = _call.DisplayName;
            _call = CallSnapshot.Empty;
        }
        _bus?.Emit("call_rejected", phone, name, "demo rejected");
        RaiseLog("[demo] incoming call declined");
        CallChanged?.Invoke(CurrentCall);
    }

    // ---------- call control ----------
    public void Hangup()
    {
        string phone, name;
        lock (_sync)
        {
            if (_call.State is CallState.Idle or CallState.Ended) return;
            phone = _call.RawNumber; name = _call.DisplayName ?? "";
            _call = _call with { State = CallState.Ended, EndedAt = DateTime.Now, IsOnHold = false };
        }
        RaiseLog("[demo] call ended");
        _bus?.Emit("call_ended", phone, name, "demo hangup");
        CallChanged?.Invoke(CurrentCall);
        _ = Task.Run(async () =>
        {
            await Task.Delay(1200);
            lock (_sync) { if (_call.State == CallState.Ended) _call = CallSnapshot.Empty; }
            CallChanged?.Invoke(CurrentCall);
        });
    }

    public void Hold() => SetHold(true);
    public void Resume() => SetHold(false);

    private void SetHold(bool hold)
    {
        lock (_sync)
        {
            if (_call.State != CallState.Active) return;
            _call = _call with { IsOnHold = hold };
        }
        RaiseLog($"[demo] hold={hold}");
        CallChanged?.Invoke(CurrentCall);
    }

    public bool ToggleHold()
    {
        var next = !(CurrentCall.State == CallState.Active && CurrentCall.IsOnHold);
        SetHold(next);
        return next;
    }

    public Task<bool> BlindTransferAsync(string target)
    {
        if (CurrentCall.State != CallState.Active) return Task.FromResult(false);
        _bus?.Emit("call_transfer", CurrentCall.RawNumber, CurrentCall.DisplayName, $"demo blind transfer to {target}");
        Hangup();
        return Task.FromResult(true);
    }

    public Task SendDtmfAsync(char key)
    {
        RaiseLog($"[demo] dtmf {key}");
        return Task.CompletedTask;
    }

    public void SetMicrophoneMuted(bool isMuted)
    {
        RaiseLog($"[demo] muted={isMuted} (mock audio)");
    }

    public void Dispose() => _bus.Dispose();

    // ---------- helpers ----------
    private void SetReg(RegistrationStatus reg)
    {
        lock (_sync) _reg = reg;
        RaiseLog($"[demo] {reg.Text}");
        RegistrationChanged?.Invoke(reg);
    }

    private void RaiseLog(string msg) => Log?.Invoke(msg);
}
