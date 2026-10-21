using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;

namespace SipSoftphone;

public sealed class HistoryItem
{
    public string Time { get; init; } = "";
    public string Direction { get; init; } = "";      // IN | OUT | ""
    public string DirectionIcon { get; init; } = "";
    public string Phone { get; init; } = "";
    public string Name { get; init; } = "";
    public string Event { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Display => string.IsNullOrWhiteSpace(Name) ? Phone : $"{Name} ({Phone})";

    /// <summary>Missed incoming calls (no answer / caller hung up before answer) — shown in red.</summary>
    public bool IsMissed => Event is "call_ring_timeout" or "call_cancelled";

    /// <summary>Human-readable call outcome label.</summary>
    public string EventLabel => Event switch
    {
        "call_outgoing" => "Outgoing",
        "call_received" => "Incoming",
        "call_rejected" => "Declined",
        "call_ring_timeout" => "Missed",
        "call_cancelled" => "Missed",
        "call_answered" => "Answered",
        "call_ended" => "Ended",
        "call_failed" => "Failed",
        "call_transfer" => "Transfer",
        "registration" => "Registration",
        _ => Event
    };

    /// <summary>Brush resource key for the direction chip (red for missed, green for outgoing, …).</summary>
    public string DirBrushKey => Direction switch
    {
        "IN" => IsMissed ? "RedBrush" : "AccentBrush",
        "OUT" => "OkBrush",
        _ => "GrayButtonBrush"
    };

    /// <summary>Vector icon resource key for the direction arrow ("" when unknown).</summary>
    public string DirIconKey => Direction switch
    {
        "IN" => "IcoArrowDownLeft",
        "OUT" => "IcoArrowUpRight",
        _ => ""
    };
}

public sealed class LogItem
{
    public string Time { get; init; } = "";
    public string Text { get; init; } = "";
    public string Kind { get; init; } = "info"; // info | call | sip | audio | error | ok
    public System.Windows.Media.Brush Brush { get; init; } = LogBrushes.Dim;
    public string Line => $"[{Time}] {Text}";
}

public static class LogBrushes
{
    public static readonly System.Windows.Media.Brush Dim = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8B, 0x98, 0xA9));
    public static readonly System.Windows.Media.Brush Ok = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3F, 0xB9, 0x50));
    public static readonly System.Windows.Media.Brush Err = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF8, 0x51, 0x49));
    public static readonly System.Windows.Media.Brush Warn = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD2, 0x99, 0x22));
    public static readonly System.Windows.Media.Brush Accent = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2F, 0x81, 0xF7));
    public static readonly System.Windows.Media.Brush Call = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE6, 0xED, 0xF3));
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppConfig _config;
    private readonly ICallService _engine;
    private readonly ContactsBook _contacts;
    private readonly RingtonePlayer _ringtone = new();
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _tickTimer;
    private bool _disposed;

    public event Action<string, string?>? IncomingCallAlert;

    public MainViewModel(AppConfig config, ICallService engine, Dispatcher dispatcher)
    {
        _config = config;
        _engine = engine;
        _dispatcher = dispatcher;

        _contacts = new ContactsBook(config.ContactsFile);
        foreach (var c in _contacts.Contacts) Contacts.Add(c);
        RebuildSpeedDials();

        _engine.Log += OnEngineLog;
        _engine.CallChanged += OnCallChanged;
        _engine.RegistrationChanged += OnRegChanged;
        _engine.IncomingAlert += OnIncomingAlert;
        if (_engine.Bus != null) _engine.Bus.Emitted += OnBusEvent;

        HistoryView = CollectionViewSource.GetDefaultView(History);
        HistoryView.Filter = FilterHistory;

        LogItemsView = CollectionViewSource.GetDefaultView(LogItems);
        LogItemsView.Filter = FilterLog;

        foreach (var a in _config.Sip.Accounts) Accounts.Add(a);
        _selectedAccountIndex = Math.Clamp(_config.Sip.ActiveAccountIndex, 0, Math.Max(0, Accounts.Count - 1));

        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tickTimer.Tick += (_, _) => OnTick();
        _tickTimer.Start();

        RegText = _engine.Registration.Text;
        RegDotBrushKey = RegDotKeyFor(_engine.Registration);
        ServerInfoText = _engine.ServerInfo;

        InitCommandFields();
    }

    // ================= registration =================

    private string _regText = "Offline";
    public string RegText { get => _regText; set => Set(ref _regText, value); }

    private string _regDotBrushKey = "TextDimBrush";
    public string RegDotBrushKey { get => _regDotBrushKey; set => Set(ref _regDotBrushKey, value); }

    private string _regExpiryText = "";
    public string RegExpiryText { get => _regExpiryText; set => Set(ref _regExpiryText, value); }

    private string _serverInfoText = "";
    public string ServerInfoText { get => _serverInfoText; set => Set(ref _serverInfoText, value); }

    public ObservableCollection<SipAccountConfig> Accounts { get; } = new();

    private int _selectedAccountIndex;
    public int SelectedAccountIndex
    {
        get => _selectedAccountIndex;
        set
        {
            if (value < 0 || value >= Accounts.Count) return;
            if (!Set(ref _selectedAccountIndex, value)) return;
            var acc = Accounts[value];
            if (string.IsNullOrWhiteSpace(acc.Server)) return;

            acc.CopyTo(_config.Sip);
            _config.Sip.ActiveAccountIndex = value;
            try { _config.Save(); } catch { }
            _ = _dispatcher.BeginInvoke(() =>
            {
                AppendLog($"[sip] switched to account {acc}", "sip");
                _engine.Restart();
                ServerInfoText = _engine.ServerInfo;
            });
        }
    }

    public bool IsRtl
    {
        get => _config.Ui.Rtl;
        set
        {
            _config.Ui.Rtl = value;
            OnPropertyChanged();
            try { _config.Save(); } catch { }
        }
    }

    public void ToggleRtl() => IsRtl = !IsRtl;

    // ================= page navigation (mobile-like tabs) =================

    private bool _isCallsPageVisible = true;
    public bool IsCallsPageVisible 
    { 
        get => _isCallsPageVisible; 
        set => Set(ref _isCallsPageVisible, value); 
    }

    private bool _isContactsPageVisible;
    public bool IsContactsPageVisible 
    { 
        get => _isContactsPageVisible; 
        set => Set(ref _isContactsPageVisible, value); 
    }

    private bool _isHistoryPageVisible;
    public bool IsHistoryPageVisible 
    { 
        get => _isHistoryPageVisible; 
        set => Set(ref _isHistoryPageVisible, value); 
    }

    private bool _isSystemLogsPageVisible;
    public bool IsSystemLogsPageVisible 
    { 
        get => _isSystemLogsPageVisible; 
        set => Set(ref _isSystemLogsPageVisible, value); 
    }

    public RelayCommand SetCallsPageCommand => new RelayCommand(() =>
    {
        IsCallsPageVisible = true;
        IsContactsPageVisible = false;
        IsHistoryPageVisible = false;
        IsSystemLogsPageVisible = false;
    });

    public RelayCommand SetContactsPageCommand => new RelayCommand(() =>
    {
        IsContactsPageVisible = true;
        IsCallsPageVisible = false;
        IsHistoryPageVisible = false;
        IsSystemLogsPageVisible = false;
    });

    public RelayCommand SetHistoryPageCommand => new RelayCommand(() =>
    {
        IsHistoryPageVisible = true;
        IsCallsPageVisible = false;
        IsContactsPageVisible = false;
        IsSystemLogsPageVisible = false;
    });

    public RelayCommand SetSystemLogsPageCommand => new RelayCommand(() =>
    {
        IsSystemLogsPageVisible = true;
        IsCallsPageVisible = false;
        IsContactsPageVisible = false;
        IsHistoryPageVisible = false;
    });

    private static string RegDotKeyFor(RegistrationStatus s)
        => s.IsOk ? "OkBrush" : s.Configured ? "ErrBrush" : "TextDimBrush";

    private void OnRegChanged(RegistrationStatus status) => _dispatcher.BeginInvoke(() =>
    {
        RegText = status.Text;
        RegDotBrushKey = RegDotKeyFor(status);
        ServerInfoText = _engine.ServerInfo;
    });

    private void OnTick()
    {
        var exp = _engine.Registration.ExpiresAt;
        if (_engine.Registration.IsOk && exp != null)
        {
            var left = exp.Value - DateTime.UtcNow;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            RegExpiryText = $"refresh in {left:mm\\:ss}";
        }
        else RegExpiryText = "";

        if (_engine.CurrentCall.State == CallState.Active) UpdateDuration(_engine.CurrentCall);
    }

    /// <summary>Re-syncs the account list after Settings changes, without restarting the engine.</summary>
    public void ReloadAccountsFromConfig()
    {
        Accounts.Clear();
        foreach (var a in _config.Sip.Accounts) Accounts.Add(a);
        _selectedAccountIndex = Math.Clamp(_config.Sip.ActiveAccountIndex, 0, Math.Max(0, Accounts.Count - 1));
        OnPropertyChanged(nameof(SelectedAccountIndex));
    }

    // ================= call state =================

    private string _displayNameText = "—";
    public string DisplayNameText
    {
        get => _displayNameText;
        set { if (Set(ref _displayNameText, value)) OnPropertyChanged(nameof(CallerInitials)); }
    }

    private string _normalizedText = "";
    public string NormalizedText { get => _normalizedText; set => Set(ref _normalizedText, value); }

    private string _rawNumberText = "";
    public string RawNumberText { get => _rawNumberText; set => Set(ref _rawNumberText, value); }

    private string _stateText = "Idle";
    public string StateText
    {
        get => _stateText;
        set { if (Set(ref _stateText, value)) OnPropertyChanged(nameof(EndCallLabel)); }
    }

    private string _durationText = "";
    public string DurationText { get => _durationText; set => Set(ref _durationText, value); }

    private string _codecText = "";
    public string CodecText { get => _codecText; set => Set(ref _codecText, value); }

    private string _callStateKey = "Idle";
    /// <summary>Idle | Ringing | Active | Ended — drives panel colors/animations.</summary>
    public string CallStateKey { get => _callStateKey; set => Set(ref _callStateKey, value); }

    public bool IsRinging => CallStateKey == "Ringing";
    public bool IsActive => CallStateKey == "Active";
    public bool IsIdle => CallStateKey == "Idle";
    public bool IsEnded => CallStateKey == "Ended";
    /// <summary>True when the last call attempt failed (rejected / unreachable / transport error).</summary>
    public bool IsFailed => CallStateKey == "Failed";
    /// <summary>True while a call is ringing or connected — drives the compact call banner.</summary>
    public bool IsOngoingCall => CallStateKey is "Ringing" or "Active";

    // Helper properties for visibility binding
    public bool IsIncoming => CallStateKey == "Ringing" && StateText.Contains("incoming");
    public bool IsCallActive => CallStateKey == "Active" || CallStateKey == "Ended";
    /// <summary>Banner answer action — only for incoming calls.</summary>
    public bool ShowBannerAnswer => IsIncoming;
    /// <summary>Banner end action — cancels an outgoing call or ends a connected one.</summary>
    public bool ShowBannerEnd => IsOngoingCall && !IsIncoming;
    /// <summary>End-call button label: "Cancel" while dialing/ringing out, "End Call" while connected.</summary>
    public string EndCallLabel => StateText.StartsWith("Calling", StringComparison.Ordinal) ? "Cancel" : "End Call";
    /// <summary>Initials of the remote party; empty for unnamed / number-only calls.</summary>
    public string CallerInitials
    {
        get
        {
            var name = DisplayNameText?.Trim() ?? "";
            if (name.Length == 0 || name == "—" || !name.Any(char.IsLetter)) return "";
            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length switch
            {
                0 => "",
                1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
                _ => (parts[0][..1] + parts[^1][..1]).ToUpperInvariant()
            };
        }
    }

    private bool _muteChecked;
    public bool MuteChecked
    {
        get => _muteChecked;
        set
        {
            if (Set(ref _muteChecked, value))
            {
                _engine.SetMicrophoneMuted(value);
                OnPropertyChanged(nameof(MuteLabel));
            }
        }
    }
    public string MuteLabel => MuteChecked ? "Unmute" : "Mute";

    private bool _holdChecked;
    public bool HoldChecked
    {
        get => _holdChecked;
        set
        {
            if (Set(ref _holdChecked, value))
            {
                if (value) _engine.Hold(); else _engine.Resume();
                OnPropertyChanged(nameof(HoldLabel));
            }
        }
    }
    public string HoldLabel => HoldChecked ? "Resume" : "Hold";

    private bool _keypadOpen = true;
    /// <summary>Visibility of the dial-pad column — toggled from the in-call controls.</summary>
    public bool KeypadOpen { get => _keypadOpen; set => Set(ref _keypadOpen, value); }

    // Hold command for UI binding
    public RelayCommand HoldCommand => new RelayCommand(() =>
    {
        HoldChecked = !HoldChecked;
    });

    private bool _transferPanelOpen;
    public bool TransferPanelOpen { get => _transferPanelOpen; set => Set(ref _transferPanelOpen, value); }

    private string _transferText = "";
    public string TransferText { get => _transferText; set => Set(ref _transferText, value); }

    private string _transferStatus = "";
    public string TransferStatus { get => _transferStatus; set => Set(ref _transferStatus, value); }

    /// <summary>Re-raises every state-derived property (used by call-state and failure transitions).</summary>
    private void RaiseCallFlags()
    {
        OnPropertyChanged(nameof(CallStateKey));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsRinging));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsEnded));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsOngoingCall));
        OnPropertyChanged(nameof(IsIncoming));
        OnPropertyChanged(nameof(IsCallActive));
        OnPropertyChanged(nameof(ShowBannerAnswer));
        OnPropertyChanged(nameof(ShowBannerEnd));
        OnPropertyChanged(nameof(EndCallLabel));
        OnPropertyChanged(nameof(CallerInitials));
    }

    private void OnCallChanged(CallSnapshot call) => _dispatcher.BeginInvoke(() => ApplyCallState(call));

    private void ApplyCallState(CallSnapshot call)
    {
        DisplayNameText = string.IsNullOrWhiteSpace(call.DisplayName)
            ? (call.RawNumber.Length > 0 ? call.RawNumber : "—")
            : call.DisplayName!;
        NormalizedText = call.NormalizedNumber.Length > 0 ? call.NormalizedNumber : "";
        RawNumberText = call.RawNumber.Length > 0 && call.RawNumber != call.NormalizedNumber ? $"raw: {call.RawNumber}" : "";
        CodecText = call.State == CallState.Active && !string.IsNullOrEmpty(call.Codec) ? call.Codec! : "";

        StateText = call.State switch
        {
            CallState.IncomingRinging => "Ringing (incoming)",
            CallState.OutgoingRinging => "Calling…",
            CallState.Active when call.IsOnHold => "On hold",
            CallState.Active => "Active",
            CallState.Ended => "Ended",
            _ => "Idle"
        };

        CallStateKey = call.State switch
        {
            CallState.IncomingRinging or CallState.OutgoingRinging => "Ringing",
            CallState.Active => "Active",
            CallState.Ended => "Ended",
            _ => "Idle"
        };
        RaiseCallFlags();

        HoldChecked = call.IsOnHold;
        if (call.State != CallState.Active)
        {
            MuteChecked = false;
            TransferPanelOpen = false;
            TransferStatus = "";
            KeypadOpen = true; // restore the dial pad once the call is over
        }

        if (call.State == CallState.Active) _tickTimer.Start();
        else { _tickTimer.Stop(); UpdateDuration(call); }

        if (call.State != CallState.IncomingRinging) _ringtone.Stop();
    }

    private void UpdateDuration(CallSnapshot call)
    {
        if (call.State == CallState.Active && call.AnsweredAt != null)
        {
            var d = DateTime.Now - call.AnsweredAt.Value;
            DurationText = d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"mm\:ss");
        }
        else if (call.State == CallState.Ended && call.AnsweredAt != null && call.EndedAt != null)
        {
            var d = call.EndedAt.Value - call.AnsweredAt.Value;
            DurationText = d.TotalSeconds >= 0
                ? (d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"mm\:ss")) + " (ended)"
                : "";
        }
        else DurationText = "";
    }

    private void OnIncomingAlert(string from, string? name)
    {
        _dispatcher.BeginInvoke(() =>
        {
            _ringtone.Play(_config.Audio.RingtonePath);
            IncomingCallAlert?.Invoke(from, name);
        });
    }

    // ================= dial =================

    private string _dialText = "";
    public string DialText
    {
        get => _dialText;
        set
        {
            if (Set(ref _dialText, value))
            {
                // typing a new number clears a previous "Failed" result
                if (CallStateKey == "Failed") ApplyCallState(_engine.CurrentCall);
                DialNormText = value.Length > 0 ? "→ " + PhoneNormalizer.Normalize(value) : "";
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    private string _dialNormText = "";
    public string DialNormText { get => _dialNormText; set => Set(ref _dialNormText, value); }

    public string DialHint => IsActive ? "in-call: pad sends DTMF" : "";

    // ================= commands =================

    public AsyncRelayCommand AnswerCommand { get; private set; } = null!;
    public RelayCommand RejectCommand { get; private set; } = null!;
    public RelayCommand HangupCommand { get; private set; } = null!;
    public AsyncRelayCommand CallCommand { get; private set; } = null!;
    public AsyncRelayCommand PadCommand { get; private set; } = null!;
    public RelayCommand BackspaceDialCommand { get; private set; } = null!;
    public RelayCommand ClearDialCommand { get; private set; } = null!;
    public RelayCommand ToggleMuteCommand { get; private set; } = null!;
    public RelayCommand ToggleHoldCommand { get; private set; } = null!;
    public AsyncRelayCommand TransferConfirmCommand { get; private set; } = null!;
    public RelayCommand ToggleTransferPanelCommand { get; private set; } = null!;
    public RelayCommand RegisterCommand { get; private set; } = null!;
    public RelayCommand UnregisterCommand { get; private set; } = null!;
    public AsyncRelayCommand RedialCommand { get; private set; } = null!;
    public AsyncRelayCommand DialContactCommand { get; private set; } = null!;
    public RelayCommand AddContactCommand { get; private set; } = null!;
    public RelayCommand RemoveContactCommand { get; private set; } = null!;
    public RelayCommand ClearLogCommand { get; private set; } = null!;
    public RelayCommand CopyLogCommand { get; private set; } = null!;
    public RelayCommand ToggleLogPauseCommand { get; private set; } = null!;
    public RelayCommand HistoryFilterCommand { get; private set; } = null!;
    public RelayCommand LogFilterCommand { get; private set; } = null!;
    public RelayCommand ClearHistoryCommand => new RelayCommand(ClearHistory);

    // ================= history =================

    public ObservableCollection<HistoryItem> History { get; } = new();
    public ICollectionView HistoryView { get; }

    private string _historyFilter = "All";
    public string HistoryFilter
    {
        get => _historyFilter;
        set { if (Set(ref _historyFilter, value)) { HistoryView?.Refresh(); RaiseHistoryEmpty(); } }
    }

    private string _historySearch = "";
    public string HistorySearch
    {
        get => _historySearch;
        set { if (Set(ref _historySearch, value)) { HistoryView?.Refresh(); RaiseHistoryEmpty(); } }
    }

    /// <summary>True when the (filtered) history list has no rows — drives the empty-state hint.</summary>
    public bool HistoryEmpty => HistoryView?.IsEmpty ?? History.Count == 0;

    private void RaiseHistoryEmpty() => OnPropertyChanged(nameof(HistoryEmpty));

    private static readonly string[] MissedEvents = { "call_ring_timeout", "call_cancelled" };
    private static readonly string[] IncomingEvents = { "call_received", "call_cancelled", "call_ring_timeout", "call_rejected" };
    private static readonly string[] OutgoingEvents = { "call_outgoing" };

    private bool FilterHistory(object o)
    {
        if (o is not HistoryItem h) return false;
        if (!string.IsNullOrWhiteSpace(HistorySearch) &&
            !h.Phone.Contains(HistorySearch, StringComparison.OrdinalIgnoreCase) &&
            !h.Name.Contains(HistorySearch, StringComparison.OrdinalIgnoreCase))
            return false;

        return HistoryFilter switch
        {
            "Missed" => MissedEvents.Contains(h.Event),
            "Incoming" => IncomingEvents.Contains(h.Event),
            "Outgoing" => OutgoingEvents.Contains(h.Event),
            _ => true
        };
    }

    /// <summary>Direction of the call currently being logged — inherited by answered/ended/failed rows.</summary>
    private string _lastDirection = "";

    private void OnBusEvent(CallEvent ev) => _dispatcher.BeginInvoke(() =>
    {
        var dir = ev.Event switch
        {
            "call_outgoing" => "OUT",
            "call_received" or "call_cancelled" or "call_ring_timeout" or "call_rejected" => "IN",
            _ => ""
        };
        if (dir.Length > 0) _lastDirection = dir;
        else dir = _lastDirection;   // answered / ended / failed / transfer belong to the call that just ran

        var icon = dir switch { "IN" => "↙", "OUT" => "↗", _ => "" };
        var t = DateTime.TryParse(ev.Ts, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToLocalTime() : DateTime.Now;

        // talk time for the history row (timestamp + duration are both required by the UI spec)
        var detail = ev.Detail;
        if (ev.Event == "call_ended")
        {
            var c = _engine.CurrentCall;
            if (c.AnsweredAt != null && c.EndedAt != null && c.EndedAt.Value >= c.AnsweredAt.Value)
            {
                var d = c.EndedAt.Value - c.AnsweredAt.Value;
                detail = "talk " + (d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"mm\:ss"));
            }
        }

        // failed call → explicit red "Failed" state in the call panel (spec state list)
        if (ev.Event == "call_failed")
        {
            CallStateKey = "Failed";
            StateText = "Failed";
            RaiseCallFlags();
        }

        History.Insert(0, new HistoryItem
        {
            Time = t.ToString("MM/dd HH:mm:ss"),
            Direction = dir,
            DirectionIcon = icon,
            Phone = ev.Phone,
            Name = ev.Name ?? "",
            Event = ev.Event,
            Detail = detail
        });
        while (History.Count > 300) History.RemoveAt(History.Count - 1);
        RaiseHistoryEmpty();
    });

    public void LoadHistoryFromFile()
    {
        try
        {
            var path = Path.IsPathRooted(_config.Hook.LogFile)
                ? _config.Hook.LogFile
                : Path.Combine(AppContext.BaseDirectory, _config.Hook.LogFile);
            if (!File.Exists(path)) return;

            var rows = new List<HistoryItem>();
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    var evt = r.GetProperty("event").GetString() ?? "";
                    if (evt == "registration") continue;
                    var phone = r.GetProperty("phone").GetString() ?? "";
                    var name = r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var detail = r.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "";
                    var tsRaw = r.GetProperty("ts").GetString() ?? "";
                    var t = DateTime.TryParse(tsRaw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                        ? dt.ToLocalTime() : DateTime.Now;
                    var dir = evt switch
                    {
                        "call_outgoing" => "OUT",
                        "call_received" or "call_cancelled" or "call_ring_timeout" or "call_rejected" => "IN",
                        _ => ""
                    };
                    rows.Add(new HistoryItem
                    {
                        Time = t.ToString("MM/dd HH:mm:ss"),
                        Direction = dir,
                        DirectionIcon = dir switch { "IN" => "↙", "OUT" => "↗", _ => "" },
                        Phone = phone,
                        Name = name,
                        Event = evt,
                        Detail = detail
                    });
                }
                catch { }
            }
            foreach (var row in rows.TakeLast(200).Reverse())
                History.Add(row);
            RaiseHistoryEmpty();
        }
        catch (Exception ex) { AppendLog($"[ui] history load failed: {ex.Message}", "error"); }
    }

    // ================= log =================

    public ObservableCollection<LogItem> LogItems { get; } = new();
    public ICollectionView LogItemsView { get; }

    private bool _logPaused;
    public bool LogPaused { get => _logPaused; set => Set(ref _logPaused, value); }

    private string _logFilter = "All";
    public string LogFilter
    {
        get => _logFilter;
        set { if (Set(ref _logFilter, value)) LogItemsView?.Refresh(); }
    }

    private bool FilterLog(object o)
    {
        if (o is not LogItem l) return false;
        return LogFilter switch
        {
            "Calls" => l.Kind == "call",
            "SIP" => l.Kind == "sip",
            "Audio" => l.Kind == "audio",
            "Errors" => l.Kind == "error",
            _ => true
        };
    }

    private void OnEngineLog(string msg) => _dispatcher.BeginInvoke(() => AppendLog(msg));

    public void AppendLog(string msg, string? kindOverride = null)
    {
        if (LogPaused && kindOverride != "error") return;

        var kind = kindOverride ?? Classify(msg);
        var brush = kind switch
        {
            "ok" => LogBrushes.Ok,
            "error" => LogBrushes.Err,
            "warn" => LogBrushes.Warn,
            "sip" => LogBrushes.Accent,
            "call" => LogBrushes.Call,
            _ => LogBrushes.Dim
        };
        LogItems.Add(new LogItem
        {
            Time = DateTime.Now.ToString("HH:mm:ss"),
            Text = msg,
            Kind = kind,
            Brush = brush
        });
        while (LogItems.Count > 500) LogItems.RemoveAt(0);

        // keep view on the latest entry
        if (LogItemsView is ListCollectionView lcv && LogItems.Count > 0)
            lcv.MoveCurrentToLast();
    }

    private static string Classify(string msg)
    {
        if (msg.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("FAILED", StringComparison.Ordinal))
            return "error";
        if (msg.Contains("registered on", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("accepted", StringComparison.OrdinalIgnoreCase))
            return "ok";
        if (msg.StartsWith("[sip", StringComparison.Ordinal)) return "sip";
        if (msg.StartsWith("[call", StringComparison.Ordinal)) return "call";
        if (msg.StartsWith("[audio", StringComparison.Ordinal)) return "audio";
        if (msg.Contains("temporary", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("busy", StringComparison.OrdinalIgnoreCase))
            return "warn";
        return "info";
    }

    // ================= contacts =================

    public ObservableCollection<ContactItem> Contacts { get; } = new();
    public ObservableCollection<ContactItem> SpeedDials { get; } = new();

    private ContactItem? _selectedContact;
    public ContactItem? SelectedContact { get => _selectedContact; set => Set(ref _selectedContact, value); }

    private string _contactSearch = "";
    public string ContactSearch
    {
        get => _contactSearch;
        set { if (Set(ref _contactSearch, value)) RebuildFilteredContacts(); }
    }

    public ObservableCollection<ContactItem> FilteredContacts { get; } = new();

    private string _newContactName = "";
    public string NewContactName { get => _newContactName; set => Set(ref _newContactName, value); }

    private string _newContactPhone = "";
    public string NewContactPhone { get => _newContactPhone; set => Set(ref _newContactPhone, value); }

    private bool _newContactSpeedDial;
    public bool NewContactSpeedDial { get => _newContactSpeedDial; set => Set(ref _newContactSpeedDial, value); }

    private void RebuildFilteredContacts()
    {
        FilteredContacts.Clear();
        foreach (var c in _contacts.Search(ContactSearch)) FilteredContacts.Add(c);
    }

    private void RebuildSpeedDials()
    {
        SpeedDials.Clear();
        foreach (var c in _contacts.SpeedDials(6)) SpeedDials.Add(c);
    }

    // ================= lifecycle =================

    public bool StartEngine()
    {
        if (!_engine.Start())
        {
            AppendLog("[ui] engine failed to start — see log above", "error");
            return false;
        }
        // Bus.Emitted is already subscribed in the constructor — re-subscribing here
        // would duplicate every history row.
        LoadHistoryFromFile();
        RebuildFilteredContacts();
        return true;
    }

    private void InitCommandFields()
    {
        AnswerCommand = new AsyncRelayCommand(() => _engine.AnswerPendingAsync(),
            () => _engine.CurrentCall.State == CallState.IncomingRinging);
        RejectCommand = new RelayCommand(() => _engine.RejectPending(),
            () => _engine.CurrentCall.State == CallState.IncomingRinging);
        HangupCommand = new RelayCommand(() => _engine.Hangup(),
            () => _engine.CurrentCall.State is CallState.IncomingRinging or CallState.OutgoingRinging or CallState.Active);
        CallCommand = new AsyncRelayCommand(async () =>
        {
            var num = DialText.Trim();
            if (num.Length == 0) return;
            await _engine.CallAsync(num);
        }, () => _engine.CurrentCall.State is CallState.Idle or CallState.Ended && DialText.Trim().Length > 0);
        PadCommand = new AsyncRelayCommand(async param =>
        {
            if (param is not string ch || ch.Length == 0) return;
            if (IsActive) { await _engine.SendDtmfAsync(ch[0]); return; }
            DialText += ch;
        });
        BackspaceDialCommand = new RelayCommand(() =>
        {
            if (DialText.Length > 0) DialText = DialText[..^1];
        });
        ClearDialCommand = new RelayCommand(() => DialText = "");
        ToggleMuteCommand = new RelayCommand(() => MuteChecked = !MuteChecked,
            () => _engine.CurrentCall.State == CallState.Active);
        ToggleHoldCommand = new RelayCommand(() =>
        {
            var onHold = _engine.ToggleHold();
            HoldChecked = onHold;
        }, () => _engine.CurrentCall.State == CallState.Active);
        TransferConfirmCommand = new AsyncRelayCommand(async () =>
        {
            var target = TransferText.Trim().Length > 0 ? TransferText.Trim() : DialText.Trim();
            if (target.Length == 0) { TransferStatus = "enter a target number"; return; }
            TransferStatus = "transferring…";
            var ok = await _engine.BlindTransferAsync(target);
            TransferStatus = ok ? "transfer accepted" : "transfer failed";
        }, () => _engine.CurrentCall.State == CallState.Active);
        ToggleTransferPanelCommand = new RelayCommand(() =>
        {
            TransferPanelOpen = !TransferPanelOpen;
            if (TransferPanelOpen && TransferText.Length == 0) TransferText = DialText;
        }, () => _engine.CurrentCall.State == CallState.Active);
        RegisterCommand = new RelayCommand(() => _engine.Register());
        UnregisterCommand = new RelayCommand(() => _engine.Unregister());
        RedialCommand = new AsyncRelayCommand(async param =>
        {
            if (param is HistoryItem h && h.Phone.Length > 0)
            {
                DialText = h.Phone;
                await _engine.CallAsync(h.Phone);
            }
        }, param => param is HistoryItem && _engine.CurrentCall.State is CallState.Idle or CallState.Ended);
        DialContactCommand = new AsyncRelayCommand(async param =>
        {
            if (param is ContactItem c && c.Phone.Length > 0)
            {
                DialText = c.Phone;
                if (_engine.CurrentCall.State is CallState.Idle or CallState.Ended)
                    await _engine.CallAsync(c.Phone);
            }
        });
        AddContactCommand = new RelayCommand(() =>
        {
            if (NewContactName.Trim().Length == 0 || NewContactPhone.Trim().Length == 0) return;
            var c = new ContactItem { Name = NewContactName.Trim(), Phone = NewContactPhone.Trim(), SpeedDial = NewContactSpeedDial };
            _contacts.Contacts.Add(c);
            Contacts.Add(c);
            _contacts.Save();
            RebuildSpeedDials();
            RebuildFilteredContacts();
            NewContactName = ""; NewContactPhone = ""; NewContactSpeedDial = false;
        });
        RemoveContactCommand = new RelayCommand(param =>
        {
            ContactItem? c = param as ContactItem ?? SelectedContact;
            if (c == null) return;
            if (_contacts.Remove(c)) Contacts.Remove(c);
            RebuildSpeedDials();
            RebuildFilteredContacts();
        });
        ClearLogCommand = new RelayCommand(() => LogItems.Clear());
        CopyLogCommand = new RelayCommand(() =>
        {
            try
            {
                var text = string.Join(Environment.NewLine, LogItems.Select(l => l.Line));
                if (text.Length > 0) System.Windows.Clipboard.SetText(text);
            }
            catch { }
        });
        ToggleLogPauseCommand = new RelayCommand(() => LogPaused = !LogPaused);
        HistoryFilterCommand = new RelayCommand(p => HistoryFilter = p as string ?? "All");
        LogFilterCommand = new RelayCommand(p => LogFilter = p as string ?? "All");
    }

    public void ClearHistory()
    {
        History.Clear();
        RaiseHistoryEmpty();
        AppendLog("[history] cleared", "ok");
    }

    public void Shutdown()
    {
        _tickTimer.Stop();
        _ringtone.Stop();
        try { _config.Save(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tickTimer.Stop();
        _ringtone.Dispose();
        _engine.Log -= OnEngineLog;
        _engine.CallChanged -= OnCallChanged;
        _engine.RegistrationChanged -= OnRegChanged;
        _engine.IncomingAlert -= OnIncomingAlert;
        if (_engine.Bus != null) _engine.Bus.Emitted -= OnBusEvent;
    }
}
