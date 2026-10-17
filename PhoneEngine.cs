using System.Net.Sockets;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace SipSoftphone;

public enum CallState { Idle, IncomingRinging, OutgoingRinging, Active, Ended }

public sealed record CallSnapshot(
    CallState State,
    string RawNumber,
    string NormalizedNumber,
    string? DisplayName,
    DateTime? AnsweredAt,
    DateTime? EndedAt)
{
    public static CallSnapshot Empty { get; } = new(CallState.Idle, "", "", null, null, null);
}

public sealed record RegistrationStatus(bool Configured, bool IsOk, string Text)
{
    public static RegistrationStatus Offline { get; } = new(false, false, "Offline");
}

/// <summary>
/// SIP engine extracted from the original console Program.cs.
/// Same registration / incoming / answer / reject / hangup flow, plus outgoing calls and real audio.
/// UI-safe: all public methods are thread-safe; events may fire on background threads.
/// </summary>
public sealed class PhoneEngine : IDisposable
{
    private readonly AppConfig _config;
    private readonly AudioEngine _audio = new();
    private readonly object _sync = new();

    private SseServer? _sse;
    private CallEventBus? _bus;
    private SIPTransport? _transport;
    private SIPUserAgent? _userAgent;
    private SIPRegistrationUserAgent? _registrar;

    private SIPServerUserAgent? _pendingUas;
    private string? _pendingFrom;
    private string? _pendingName;
    private RTPSession? _currentSession;
    private CallSnapshot _call = CallSnapshot.Empty;
    private bool _disposed;

    public CallSnapshot CurrentCall { get { lock (_sync) return _call; } }
    public RegistrationStatus Registration { get; private set; } = RegistrationStatus.Offline;
    public bool IsSseRunning => _sse != null;
    public string SseUrl => $"http://127.0.0.1:{_config.Hook.SsePort}/events";
    public CallEventBus? Bus => _bus;
    public AudioEngine Audio => _audio;
    public string ServerInfo => string.IsNullOrWhiteSpace(_config.Sip.Server)
        ? "listen-only (no server configured)"
        : $"{_config.Sip.Server}:{_config.Sip.Port} as {_config.Sip.Username}";

    public event Action<string>? Log;
    public event Action<CallSnapshot>? CallChanged;
    public event Action<RegistrationStatus>? RegistrationChanged;
    public event Action<string, string?>? IncomingAlert;

    public PhoneEngine(AppConfig config) => _config = config;

    public bool Start()
    {
        lock (_sync)
        {
            if (_disposed) return false;

            if (_config.Hook.SseEnabled)
            {
                try
                {
                    _sse = new SseServer(_config.Hook.SsePort);
                    _sse.Start();
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"[sse] start failed: {ex.Message}");
                    _sse = null;
                }
            }

            _bus = new CallEventBus(_config.Hook, _sse);

            _transport = new SIPTransport();
            if (_config.Sip.Trace) _transport.EnableTraceLogs();

            try
            {
                var channel = _transport.CreateChannel(SIPProtocolsEnum.udp, AddressFamily.InterNetwork, _config.Sip.Port);
                _transport.AddSIPChannel(channel);
                Log?.Invoke($"[sip] listening UDP :{_config.Sip.Port}");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[sip] bind failed: {ex.Message}");
                return false;
            }

            _userAgent = new SIPUserAgent(_transport, null, false, null);
            _userAgent.OnIncomingCall += OnIncomingCall;
            _userAgent.ServerCallCancelled += OnServerCallCancelled;
            _userAgent.ServerCallRingTimeout += OnServerCallRingTimeout;
            _userAgent.OnCallHungup += OnCallHungup;
            _userAgent.ClientCallAnswered += OnClientCallAnswered;
            _userAgent.ClientCallFailed += OnClientCallFailed;

            StartRegistration();
            return true;
        }
    }

    private void StartRegistration()
    {
        if (_transport == null || _userAgent == null) return;

        if (string.IsNullOrWhiteSpace(_config.Sip.Server) || string.IsNullOrWhiteSpace(_config.Sip.Username))
        {
            SetRegistration(new RegistrationStatus(false, false, "Listen-only"));
            Log?.Invoke("[sip] no server/username in config — running in listen-only mode (still answers incoming calls)");
            return;
        }

        _registrar = new SIPRegistrationUserAgent(
            _transport,
            _config.Sip.Username,
            _config.Sip.Password,
            _config.Sip.Server,
            _config.Sip.ExpirySeconds,
            30, 30, 3, false, true);

        _registrar.RegistrationSuccessful += (uri, resp) =>
        {
            Log?.Invoke($"[sip] registered on {_config.Sip.Server} as {_config.Sip.Username}");
            SetRegistration(new RegistrationStatus(true, true, "Registered"));
            BusEmit("registration", _config.Sip.Username, null, $"registered (expires {_config.Sip.ExpirySeconds}s)");
            Trigger("registration", _config.Sip.Username);
        };
        _registrar.RegistrationFailed += (uri, resp, err) =>
        {
            Log?.Invoke($"[sip] registration FAILED: {err} {resp?.Status}");
            SetRegistration(new RegistrationStatus(true, false, "Failed"));
            BusEmit("registration", _config.Sip.Username, null, $"failed: {err}");
        };
        _registrar.RegistrationTemporaryFailure += (uri, resp, err) =>
        {
            Log?.Invoke($"[sip] registration temporary failure: {err}");
        };

        _registrar.Start();
        SetRegistration(new RegistrationStatus(true, false, "Registering…"));
        Log?.Invoke($"[sip] registering on {_config.Sip.Server} as {_config.Sip.Username} ...");
    }

    // ---------- incoming ----------

    private void OnIncomingCall(SIPUserAgent ua, SIPRequest invite)
    {
        var fromUri = invite?.Header.From?.FromURI;
        var from = fromUri?.UnescapedUser ?? fromUri?.HostAddress ?? "unknown";
        var name = string.IsNullOrWhiteSpace(invite?.Header.From?.FromName) ? null : invite!.Header.From!.FromName;

        var accepted = ua.AcceptCall(invite!);
        try { accepted.Progress(SIPResponseStatusCodesEnum.Ringing, null, null, null, null); } catch { }

        lock (_sync)
        {
            _pendingUas = accepted;
            _pendingFrom = from;
            _pendingName = name;
            SetCallLocked(new CallSnapshot(CallState.IncomingRinging, from, PhoneNormalizer.Normalize(from), name, null, null));
        }

        BusEmit("call_received", from, name, "incoming INVITE");
        Trigger("call_received", from);
        IncomingAlert?.Invoke(from, name);

        if (_config.Sip.AutoAnswer)
            _ = AnswerPendingAsync();
    }

    private void OnServerCallCancelled(ISIPServerUserAgent uas, SIPRequest cancelRequest)
    {
        string from;
        string? name;
        lock (_sync)
        {
            if (!ReferenceEquals(uas, _pendingUas)) return;
            from = _pendingFrom ?? "";
            name = _pendingName;
            _pendingUas = null; _pendingFrom = null; _pendingName = null;
            SetCallLocked(new CallSnapshot(CallState.Ended, from, PhoneNormalizer.Normalize(from), name, null, DateTime.Now));
        }
        BusEmit("call_cancelled", from, name, "caller hung up before answer");
    }

    private void OnServerCallRingTimeout(ISIPServerUserAgent uas)
    {
        string from;
        string? name;
        lock (_sync)
        {
            if (!ReferenceEquals(uas, _pendingUas)) return;
            from = _pendingFrom ?? "";
            name = _pendingName;
            _pendingUas = null; _pendingFrom = null; _pendingName = null;
            SetCallLocked(new CallSnapshot(CallState.Ended, from, PhoneNormalizer.Normalize(from), name, null, DateTime.Now));
        }
        BusEmit("call_ring_timeout", from, name, "no answer");
    }

    private void OnCallHungup(SIPDialogue? dialogue)
    {
        var who = dialogue?.RemoteUserField?.Name ?? "";
        var uri = dialogue?.RemoteUserField?.URI?.UnescapedUser ?? "";
        lock (_sync)
        {
            _audio.Stop();
            CloseSessionLocked();
            _pendingUas = null; _pendingFrom = null; _pendingName = null;
            var cur = _call;
            SetCallLocked(cur with { State = CallState.Ended, EndedAt = DateTime.Now });
        }
        BusEmit("call_ended", uri, string.IsNullOrEmpty(who) ? null : who, "dialogue closed");
    }

    // ---------- outgoing ----------

    public async Task<bool> CallAsync(string number)
    {
        var dst = number.Trim();
        if (string.IsNullOrEmpty(dst)) { Log?.Invoke("[call] empty number"); return false; }

        lock (_sync)
        {
            if (_userAgent == null) { Log?.Invoke("[call] engine not started"); return false; }
            if (_userAgent.IsCallActive || _userAgent.IsCalling || _userAgent.IsRinging || _pendingUas != null)
            {
                Log?.Invoke("[call] busy — another call in progress");
                return false;
            }
        }

        var session = CreateSession();
        await session.Start();

        var target = dst.Contains('@') || dst.StartsWith("sip:", StringComparison.OrdinalIgnoreCase)
            ? dst
            : $"{dst}@{_config.Sip.Server}";

        lock (_sync)
        {
            _currentSession = session;
            SetCallLocked(new CallSnapshot(CallState.OutgoingRinging, dst, PhoneNormalizer.Normalize(dst), null, null, null));
        }

        BusEmit("call_outgoing", dst, null, $"dialing {target}");
        Log?.Invoke($"[call] dialing {target}");

        _ = Task.Run(async () =>
        {
            try
            {
                var okCall = await _userAgent!.Call(target, _config.Sip.Username, _config.Sip.Password, session);
                if (!okCall)
                {
                    Log?.Invoke("[call] dial failed or rejected by remote");
                    lock (_sync)
                    {
                        _audio.Stop();
                        CloseSessionLocked();
                        var cur = _call;
                        SetCallLocked(cur with { State = CallState.Ended, EndedAt = DateTime.Now });
                    }
                    BusEmit("call_failed", dst, null, "dial failed");
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[call] dial failed: {ex.Message}");
                lock (_sync)
                {
                    _audio.Stop();
                    CloseSessionLocked();
                    SetCallLocked(CallSnapshot.Empty);
                }
                BusEmit("call_failed", dst, null, ex.Message);
            }
        });
        return true;
    }

    private void OnClientCallAnswered(ISIPClientUserAgent uac, SIPResponse response)
    {
        lock (_sync)
        {
            if (_currentSession == null) return;
            _audio.Attach(_currentSession, _config.Audio);
            _audio.StartMic(_config.Audio);
            SetCallLocked(_call with { State = CallState.Active, AnsweredAt = DateTime.Now });
        }
        BusEmit("call_answered", _call.RawNumber, _call.DisplayName, "remote answered");
        Trigger("call_answered", _call.RawNumber);
        Log?.Invoke("[call] active — real G.711 audio flowing");
    }

    private void OnClientCallFailed(ISIPClientUserAgent uac, string errorMessage, SIPResponse? response)
    {
        Log?.Invoke($"[call] call failed: {errorMessage} {response?.Status}");
        lock (_sync)
        {
            _audio.Stop();
            CloseSessionLocked();
            var cur = _call;
            SetCallLocked(cur with { State = CallState.Ended, EndedAt = DateTime.Now });
        }
        BusEmit("call_failed", _call.RawNumber, _call.DisplayName, errorMessage);
    }

    // ---------- actions ----------

    public async Task AnswerPendingAsync()
    {
        SIPServerUserAgent? uas;
        string from; string? name;
        lock (_sync)
        {
            uas = _pendingUas;
            if (uas == null)
            {
                Log?.Invoke("[call] no incoming call to answer");
                return;
            }
            from = _pendingFrom ?? "";
            name = _pendingName;
            _pendingUas = null;
        }

        try
        {
            var session = CreateSession();
            await session.Start();
            lock (_sync) _audio.Attach(session, _config.Audio);
            await _userAgent!.Answer(uas, session, null);

            lock (_sync)
            {
                _currentSession = session;
                _pendingFrom = null; _pendingName = null;
                _audio.StartMic(_config.Audio);
                SetCallLocked(_call with { State = CallState.Active, AnsweredAt = DateTime.Now });
            }

            BusEmit("call_answered", from, name, "user answered");
            Trigger("call_answered", from);
            Log?.Invoke("[call] answered — real G.711 audio active (mic + speaker)");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[call] answer failed: {ex.Message}");
            lock (_sync) _pendingUas = uas;
        }
    }

    public void RejectPending()
    {
        SIPServerUserAgent? uas;
        string from; string? name;
        lock (_sync)
        {
            uas = _pendingUas;
            if (uas == null) { Log?.Invoke("[call] no incoming call to reject"); return; }
            _pendingUas = null;
            from = _pendingFrom ?? "";
            name = _pendingName;
            _pendingFrom = null; _pendingName = null;
            SetCallLocked(new CallSnapshot(CallState.Ended, from, PhoneNormalizer.Normalize(from), name, null, DateTime.Now));
        }
        try { uas.Reject(SIPResponseStatusCodesEnum.BusyHere, "Busy"); } catch { }
        BusEmit("call_rejected", from, name, "rejected with 486");
    }

    public void Hangup()
    {
        lock (_sync)
        {
            if (_userAgent != null && (_userAgent.IsCallActive || _userAgent.IsCalling || _userAgent.IsRinging))
            {
                try { _userAgent.Hangup(); } catch { }
                Log?.Invoke("[call] hung up");
                return;
            }
            if (_pendingUas != null)
            {
                // fall through to reject outside lock
            }
            else
            {
                Log?.Invoke("[call] nothing to hang up");
                return;
            }
        }
        RejectPending();
    }

    // ---------- internals ----------

    private static RTPSession CreateSession()
    {
        var session = new RTPSession(false, false, false, null, 0, null);
        session.addTrack(new MediaStreamTrack(new[] { SDPWellKnownMediaFormatsEnum.PCMA }));
        return session;
    }

    private void CloseSessionLocked()
    {
        var s = _currentSession;
        _currentSession = null;
        if (s == null) return;
        try { s.Close("hangup"); } catch { }
        try { s.Dispose(); } catch { }
    }

    private void BusEmit(string evt, string phone, string? name, string detail)
        => _bus?.Emit(evt, phone, name, detail);

    private void Trigger(string evt, string phone)
        => ChromeTrigger.Fire(_config.Trigger, phone, evt);

    private void SetRegistration(RegistrationStatus status)
    {
        Registration = status;
        RegistrationChanged?.Invoke(status);
    }

    private void SetCallLocked(CallSnapshot snap)
    {
        _call = snap;
        var s = snap;
        _ = Task.Run(() => CallChanged?.Invoke(s));
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _audio.Dispose();
            try { _userAgent?.Close(); } catch { }
            try { _registrar?.Stop(false); } catch { }
            try { _transport?.Shutdown(); } catch { }
            CloseSessionLocked();
            _sse?.Dispose();
            _bus?.Dispose();
        }
    }
}
