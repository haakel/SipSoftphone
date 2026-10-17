using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Media;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace SipSoftphone;

public partial class MainWindow : Window
{
    private readonly AppConfig _config;
    private readonly PhoneEngine _engine;
    private readonly ObservableCollection<HistoryRow> _history = new();
    private readonly DispatcherTimer _durationTimer;
    private bool _shutdown;

    public ObservableCollection<HistoryRow> History => _history;

    public MainWindow(AppConfig config)
    {
        InitializeComponent();
        DataContext = this;
        _config = config;

        _engine = new PhoneEngine(config);
        _engine.Log += OnLog;
        _engine.CallChanged += OnCallChanged;
        _engine.RegistrationChanged += OnRegChanged;
        _engine.IncomingAlert += OnIncomingAlert;

        _durationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _durationTimer.Tick += (_, _) => UpdateDuration();

        Loaded += OnWindowLoaded;
        Closing += OnWindowClosing;

        LoadHistoryFromFile();
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        Diag.Sink = msg =>
        {
            try { Dispatcher.BeginInvoke(() => AppendLog(msg)); }
            catch { }
        };

        ServerInfoText.Text = _engine.ServerInfo;

        if (!_engine.Start())
        {
            AppendLog("[ui] engine failed to start — see log above");
            return;
        }

        if (_engine.Bus != null)
            _engine.Bus.Emitted += OnBusEvent;

        _durationTimer.Start();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdown) return;
        _shutdown = true;
        _durationTimer.Stop();
        Diag.Sink = static msg => Console.WriteLine(msg);
        _engine.Dispose();
    }

    // ---------- logging ----------

    private void OnLog(string msg) => Dispatcher.BeginInvoke(() => AppendLog(msg));

    private void AppendLog(string msg)
    {
        LogBox.AppendText(msg + Environment.NewLine);
        if (LogBox.LineCount > 500)
        {
            var lines = LogBox.Text.Split('\n');
            LogBox.Text = string.Join('\n', lines.Skip(lines.Length - 400));
        }
        LogBox.ScrollToEnd();
    }

    // ---------- call state ----------

    private void OnCallChanged(CallSnapshot call) => Dispatcher.BeginInvoke(() => ApplyCallState(call));

    private void ApplyCallState(CallSnapshot call)
    {
        DisplayNameText.Text = string.IsNullOrWhiteSpace(call.DisplayName) ? (call.RawNumber.Length > 0 ? call.RawNumber : "—") : call.DisplayName;
        NormalizedText.Text = call.NormalizedNumber.Length > 0 ? call.NormalizedNumber : "";
        RawNumberText.Text = call.RawNumber.Length > 0 && call.RawNumber != call.NormalizedNumber ? $"raw: {call.RawNumber}" : "";

        StateText.Text = call.State switch
        {
            CallState.IncomingRinging => "Ringing (incoming)",
            CallState.OutgoingRinging => "Calling…",
            CallState.Active => "Active",
            CallState.Ended => "Ended",
            _ => "Idle"
        };

        AnswerButton.IsEnabled = call.State == CallState.IncomingRinging;
        RejectButton.IsEnabled = call.State == CallState.IncomingRinging;
        HangupButton.IsEnabled = call.State is CallState.IncomingRinging or CallState.OutgoingRinging or CallState.Active;
        CallButton.IsEnabled = call.State is CallState.Idle or CallState.Ended;

        if (call.State == CallState.Active)
            _durationTimer.Start();
        else
        {
            _durationTimer.Stop();
            UpdateDuration(call);
        }
    }

    private void UpdateDuration(CallSnapshot? call = null)
    {
        call ??= _engine.CurrentCall;
        if (call.State == CallState.Active && call.AnsweredAt != null)
        {
            var d = DateTime.Now - call.AnsweredAt.Value;
            DurationText.Text = d.ToString(@"mm\:ss");
        }
        else if (call.State == CallState.Ended && call.AnsweredAt != null && call.EndedAt != null)
        {
            var d = call.EndedAt.Value - call.AnsweredAt.Value;
            DurationText.Text = d.TotalSeconds >= 0 ? d.ToString(@"mm\:ss") + " (ended)" : "";
        }
        else if (call.State == CallState.OutgoingRinging || call.State == CallState.IncomingRinging)
        {
            DurationText.Text = "";
        }
        else
        {
            DurationText.Text = "";
        }
    }

    private void OnRegChanged(RegistrationStatus status) => Dispatcher.BeginInvoke(() =>
    {
        RegText.Text = status.Text;
        RegDot.Fill = (System.Windows.Media.Brush)FindResource(
            status.IsOk ? "OkBrush" : status.Configured ? "ErrBrush" : "TextDimBrush");
        ServerInfoText.Text = _engine.ServerInfo;
    });

    private void OnIncomingAlert(string from, string? name)
    {
        Dispatcher.BeginInvoke(() =>
        {
            SystemSounds.Exclamation.Play();
            FlashWindow();
        });
    }

    private void FlashWindow()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var info = new NativeMethods.FLASHWINFO
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.FLASHWINFO>(),
                hwnd = hwnd,
                dwFlags = NativeMethods.FLASHW_ALL | NativeMethods.FLASHW_TIMERNOFG,
                uCount = 5,
                dwTimeout = 0
            };
            NativeMethods.FlashWindowEx(ref info);
        }
        catch { }
    }

    // ---------- history ----------

    private void OnBusEvent(CallEvent ev) => Dispatcher.BeginInvoke(() =>
    {
        var dir = ev.Event switch
        {
            "call_outgoing" => "OUT",
            "call_received" or "call_cancelled" or "call_ring_timeout" or "call_rejected" => "IN",
            _ => ""
        };
        var t = DateTime.TryParse(ev.Ts, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToLocalTime() : DateTime.Now;
        _history.Insert(0, new HistoryRow(t.ToString("HH:mm:ss"), dir, ev.Phone, ev.Event, ev.Detail));
        while (_history.Count > 300) _history.RemoveAt(_history.Count - 1);
    });

    private void LoadHistoryFromFile()
    {
        try
        {
            var path = Path.IsPathRooted(_config.Hook.LogFile)
                ? _config.Hook.LogFile
                : Path.Combine(AppContext.BaseDirectory, _config.Hook.LogFile);
            if (!File.Exists(path)) return;

            var rows = new List<HistoryRow>();
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    var evt = r.GetProperty("event").GetString() ?? "";
                    if (evt == "registration") continue;
                    var phone = r.GetProperty("phone").GetString() ?? "";
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
                    rows.Add(new HistoryRow(t.ToString("HH:mm:ss"), dir, phone, evt, detail));
                }
                catch { }
            }
            foreach (var row in rows.TakeLast(200).Reverse())
                _history.Add(row);
        }
        catch (Exception ex) { AppendLog($"[ui] history load failed: {ex.Message}"); }
    }

    // ---------- actions ----------

    private async void OnAnswerClick(object sender, RoutedEventArgs e) => await _engine.AnswerPendingAsync();

    private void OnRejectClick(object sender, RoutedEventArgs e) => _engine.RejectPending();

    private void OnHangupClick(object sender, RoutedEventArgs e) => _engine.Hangup();

    private async void OnCallClick(object sender, RoutedEventArgs e)
    {
        var num = DialBox.Text.Trim();
        if (num.Length == 0) return;
        await _engine.CallAsync(num);
    }

    private async void OnDialKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DialBox.Text.Trim().Length > 0)
        {
            await _engine.CallAsync(DialBox.Text.Trim());
            e.Handled = true;
        }
    }

    private void OnDialTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => DialNormText.Text = DialBox.Text.Length > 0 ? "→ " + PhoneNormalizer.Normalize(DialBox.Text) : "";

    private void OnPadClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && b.Content is string ch)
        {
            DialBox.Text += ch;
            DialBox.CaretIndex = DialBox.Text.Length;
            DialNormText.Text = PhoneNormalizer.Normalize(DialBox.Text);
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_config) { Owner = this };
        if (win.ShowDialog() == true)
        {
            AppendLog("[ui] settings saved — restart the app to apply SIP/SSE changes");
            ServerInfoText.Text = _engine.ServerInfo;
        }
    }
}

public sealed record HistoryRow(string Time, string Direction, string Phone, string Event, string Detail);

internal static class NativeMethods
{
    internal const uint FLASHW_ALL = 3;
    internal const uint FLASHW_TIMERNOFG = 12;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
}
