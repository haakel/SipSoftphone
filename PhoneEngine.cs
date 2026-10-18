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
    DateTime? EndedAt,
    bool IsOnHold = false,
    string? Codec = null)
{
    public static CallSnapshot Empty { get; } = new(CallState.Idle, "", "", null, null, null);
}

public sealed record RegistrationStatus(bool Configured, bool IsOk, string Text, DateTime? ExpiresAt = null)
{
    public static RegistrationStatus Offline { get; } = new(false, false, "Offline");
}

/// <summary>
/// SIP engine extracted from the original console Program.cs.
/// Registration / incoming / answer / reject / hangup, outgoing calls, real audio,
/// hold, blind transfer, DTMF, manual register/unregister, TCP/TLS, restart.
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
    private bool _started;

    public CallSnapshot CurrentCall { get { lock (_sync) return _call; } }
    public RegistrationStatus Registration { get; private set; } = RegistrationStatus.Offline;
    public bool IsSseRunning => _sse != null;
    public string SseUrl => $"http://127.0.0.1:{_config.Hook.SsePort}/events";
    public CallEventBus? Bus => _bus;
    public AudioEngine Audio => _audio;
    public bool IsOnLocalHold { get { lock (_sync) return _userAgent?.IsOnLocalHold == true; } }
    public string ServerInfo => string.IsNullOrWhiteSpace(_config.Sip.Server)
        ? "listen-only (no server configured)"
        : $"{_config.Sip.Server}:{_config.Sip.Port}/{_config.Sip.Transport?.ToUpperInvariant()} as {_config.Sip.Username}";

    public event Action<string>? Log;
    public event Action<CallSnapshot>? CallChanged;
    public event Action<RegistrationStatus>? RegistrationChanged;
    public event Action<string, string?>? IncomingAlert;

    public PhoneEngine(AppConfig config) => _config = config;

    public bool Start()
    {
        lock (_sync)
        {
            if (_disposed || _started) return !_disposed;
            _started = true;

            if (_config.Hook.SseEnabled && _sse == null)
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

            _bus ??= new CallEventBus(_config.Hook, _sse);

            _transport = new SIPTransport();
            if (_config.Sip.Trace) _transport.EnableTraceLogs();

            var proto = ParseProtocol(_config.Sip.Transport);
            try
            {
                var channel = _transport.CreateChannel(proto, AddressFamily.InterNetwork, _config.Sip.Port);
                _transport.AddSIPChannel(channel);
                Log?.Invoke($"[sip] listening {proto.ToString().ToUpperInvariant()} :{_config.Sip.Port}");
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

    /// <summary>Fully stops SIP/SSE (keeps the object reusable) — used before Start() again.</summary>
    public void Stop()
    {
        lock (_sync)
        {
            if (!_started) return;
            _started = false;

            _audio.Stop();
            CloseSessionLocked();
            _pendingUas = null; _pendingFrom = null; _pendingName = null;
            _call = CallSnapshot.Empty;

            try { _userAgent?.Close(); } catch { }
            _userAgent = null;
            try { _registrar?.Stop(false); } catch { }
            _registrar = null;
            try { _transport?.Shutdown(); } catch { }
            _transport = null;

            SetRegistration(RegistrationStatus.Offline);
        }
    }

    /// <summary>Restarts SIP (and SSE) — used after settings/account changes.</summary>
    public bool Restart()
    {
        Stop();
        return Start();
    }

    private static SIPProtocolsEnum ParseProtocol(string? t) => t?.ToLowerInvariant() switch
    {
        "tcp" => SIPProtocolsEnum.tcp,
        "tls" => SIPProtocolsEnum.tls,
        _ => SIPProtocolsEnum.udp
    };

    // ---------- registration ----------

    public void Register()
    {
        lock (_sync)
        {
            if (!_started || _disposed) { Log?.Invoke("[sip] engine not started"); return; }
            try { _registrar?.Stop(false); } catch { }
            _registrar = null;
            StartRegistration();
        }
    }

    public void Unregister()
    {
        lock (_sync)
        {
            if (_registrar != null)
            {
                try { _registrar.Stop(true); } catch { }
                _registrar = null;
            }
            SetRegistration(new RegistrationStatus(true, false, "Unregistered"));
            Log?.Invoke("[sip] unregistered manually");
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
            var expires = DateTime.UtcNow.AddSeconds(Math.Max(30, _config.Sip.ExpirySeconds));
            Log?.Invoke($"[sip] registered on {_config.Sip.Server} as {_config.Sip.Username}");
            SetRegistration(new RegistrationStatus(true, true, "Registered", expires));
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
        Log?.Invoke($"[sip] registering on {_config.Sip.Server} as {_config.Sip.Username} ({_config.Sip.Transport}) ...");
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
            _audio.SetMuted(false);
            CloseSessionLocked();
            _pendingUas = null; _pendingFrom = null; _pendingName = null;
            var cur = _call;
            SetCallLocked(cur with { State = CallState.Ended, EndedAt = DateTime.Now, IsOnHold = false });
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
            SetCallLocked(_call with { State = CallState.Active, AnsweredAt = DateTime.Now, Codec = ReadCodec(_currentSession) });
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

    private static string? ReadCodec(RTPSession session)
    {
        try
        {
            var fmtOpt = session.AudioStream?.GetSendingFormat();
            if (fmtOpt is not { } fmt || fmt.ID < 0) return null;
            var name = fmt.Name();
            var rate = fmt.ClockRate();
            return rate > 0 ? $"{name} {rate / 1000}kHz" : name;
        }
        catch { return null; }
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
                SetCallLocked(_call with { State = CallState.Active, AnsweredAt = DateTime.Now, Codec = ReadCodec(session) });
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

    /// <summary>Puts the remote party on hold (re-INVITE sendonly) and mutes the mic.</summary>
    public void Hold()
    {
        SIPUserAgent? ua;
        lock (_sync)
        {
            ua = _userAgent;
            if (ua == null || !ua.IsCallActive) { Log?.Invoke("[call] no active call to hold"); return; }
            if (ua.IsOnLocalHold) return;
        }
        try
        {
            ua.PutOnHold();
            _audio.SetMuted(true);
            Log?.Invoke("[call] on hold");
            lock (_sync) SetCallLocked(_call with { IsOnHold = true });
        }
        catch (Exception ex) { Log?.Invoke($"[call] hold failed: {ex.Message}"); }
    }

    public void Resume()
    {
        SIPUserAgent? ua;
        lock (_sync)
        {
            ua = _userAgent;
            if (ua == null || !ua.IsOnLocalHold) return;
        }
        try
        {
            ua.TakeOffHold();
            _audio.SetMuted(false);
            Log?.Invoke("[call] resumed from hold");
            lock (_sync) SetCallLocked(_call with { IsOnHold = false });
        }
        catch (Exception ex) { Log?.Invoke($"[call] resume failed: {ex.Message}"); }
    }

    /// <summary>Toggles local hold. Returns true if call is now on hold.</summary>
    public bool ToggleHold()
    {
        if (IsOnLocalHold) { Resume(); return false; }
        Hold();
        return IsOnLocalHold;
    }

    /// <summary>Blind transfer (SIP REFER) of the current call to <paramref name="target"/>.</summary>
    public async Task<bool> BlindTransferAsync(string target)
    {
        var dst = target.Trim();
        if (string.IsNullOrEmpty(dst)) { Log?.Invoke("[call] transfer: empty target"); return false; }

        SIPUserAgent? ua;
        lock (_sync)
        {
            ua = _userAgent;
            if (ua == null || !ua.IsCallActive) { Log?.Invoke("[call] transfer: no active call"); return false; }
        }

        var uriStr = dst.Contains('@') || dst.StartsWith("sip:", StringComparison.OrdinalIgnoreCase)
            ? (dst.StartsWith("sip:", StringComparison.OrdinalIgnoreCase) ? dst : "sip:" + dst)
            : $"sip:{dst}@{_config.Sip.Server}";

        SIPURI? uri;
        try { uri = SIPURI.ParseSIPURI(uriStr); }
        catch (Exception ex)
        {
            Log?.Invoke($"[call] transfer: invalid target {uriStr} ({ex.Message})");
            return false;
        }
        if (uri == null)
        {
            Log?.Invoke($"[call] transfer: invalid target {uriStr}");
            return false;
        }

        Log?.Invoke($"[call] blind transfer to {uriStr} ...");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var ok = await ua.BlindTransfer(uri, TimeSpan.FromSeconds(15), cts.Token, null, _config.Sip.Username, _config.Sip.Password);
            Log?.Invoke(ok ? "[call] transfer accepted" : "[call] transfer rejected/failed");
            BusEmit("call_transfer", _call.RawNumber, _call.DisplayName, ok ? $"blind transfer to {dst}" : $"transfer failed to {dst}");
            return ok;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[call] transfer failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Sends a DTMF digit (RFC2833 RTP event) during an active call.</summary>
    public async Task SendDtmfAsync(char key)
    {
        byte evt = key switch
        {
            >= '0' and <= '9' => (byte)(key - '0'),
            '*' => 10,
            '#' => 11,
            _ => byte.MaxValue
        };
        if (evt == byte.MaxValue) { Log?.Invoke($"[call] invalid DTMF key '{key}'"); return; }

        RTPSession? session;
        lock (_sync)
        {
            if (_call.State != CallState.Active) return;
            session = _currentSession;
        }
        if (session == null) return;

        Log?.Invoke($"[call] DTMF {key}");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await session.SendDtmf(evt, cts.Token);
        }
        catch (Exception ex) { Log?.Invoke($"[call] DTMF failed: {ex.Message}"); }
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

    /// <summary>SIP OPTIONS reachability test against a server (no registration needed).</summary>
    public static async Task<string> TestServerAsync(string server, int port, string? transport,
        string? user = null, int timeoutMs = 5000)
    {
        if (string.IsNullOrWhiteSpace(server)) return "FAIL: no server";
        var proto = (transport ?? "udp").ToLowerInvariant();
        if (proto is not ("udp" or "tcp" or "tls")) proto = "udp";

        var fromUser = string.IsNullOrWhiteSpace(user) ? "test" : user!;
        var branch = "z9hG4bK" + Guid.NewGuid().ToString("N")[..16];
        var tag = Guid.NewGuid().ToString("N")[..12];
        var callId = Guid.NewGuid().ToString("N");
        var viaProto = proto.ToUpperInvariant();

        var request =
            $"OPTIONS sip:{server} SIP/2.0\r\n" +
            $"Via: SIP/2.0/{viaProto} {server}:{port};branch={branch}\r\n" +
            "Max-Forwards: 70\r\n" +
            $"From: <sip:{fromUser}@{server}>;tag={tag}\r\n" +
            $"To: <sip:{server}>\r\n" +
            $"Call-ID: {callId}\r\n" +
            "CSeq: 1 OPTIONS\r\n" +
            $"Contact: <sip:{fromUser}@{server}:{port}>\r\n" +
            "Accept: application/sdp\r\n" +
            "Content-Length: 0\r\n\r\n";
        var bytes = System.Text.Encoding.ASCII.GetBytes(request);

        try
        {
            if (proto == "udp")
            {
                using var udp = new System.Net.Sockets.UdpClient();
                udp.Client.ReceiveTimeout = timeoutMs;
                await udp.SendAsync(bytes, bytes.Length, server, port);
                var res = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
                var text = System.Text.Encoding.ASCII.GetString(res.Buffer);
                return ParseSipStatusLine(text);
            }

            using var client = new System.Net.Sockets.TcpClient();
            var connectTask = client.ConnectAsync(server, port);
            if (await Task.WhenAny(connectTask, Task.Delay(timeoutMs)) != connectTask)
                return $"FAIL: TCP connect timeout ({timeoutMs} ms)";
            await connectTask;

            System.IO.Stream stream = client.GetStream();
            if (proto == "tls")
            {
                var ssl = new System.Net.Security.SslStream(stream, false, (_, _, _, _) => true);
                await ssl.AuthenticateAsClientAsync(server);
                stream = ssl;
            }

            await stream.WriteAsync(bytes);
            await stream.FlushAsync();

            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            var buffer = new byte[8192];
            var sb = new System.Text.StringBuilder();
            while (DateTime.UtcNow < deadline)
            {
                var readTask = stream.ReadAsync(buffer.AsMemory(0, buffer.Length)).AsTask();
                var done = await Task.WhenAny(readTask, Task.Delay(100));
                if (done != readTask) continue;
                var n = await readTask;
                if (n <= 0) break;
                sb.Append(System.Text.Encoding.ASCII.GetString(buffer, 0, n));
                if (sb.ToString().Contains("\r\n\r\n")) break;
            }
            var body = sb.ToString();
            return body.Length == 0 ? $"FAIL: no response ({timeoutMs} ms)" : ParseSipStatusLine(body);
        }
        catch (Exception ex)
        {
            return $"FAIL: {ex.Message}";
        }
    }

    private static string ParseSipStatusLine(string sipMessage)
    {
        var firstLine = sipMessage.Split('\n').FirstOrDefault()?.Trim('\r', ' ') ?? "";
        if (firstLine.StartsWith("SIP/2.0 ", StringComparison.Ordinal))
        {
            var rest = firstLine["SIP/2.0 ".Length..].Trim();
            if (rest.Length >= 3 && int.TryParse(rest[..3], out var code))
                return code is >= 200 and < 300 ? $"OK: {rest}" : $"FAIL: {rest}";
            return $"FAIL: {rest}";
        }
        return $"FAIL: unexpected response ({firstLine})";
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
