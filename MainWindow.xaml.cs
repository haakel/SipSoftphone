using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace SipSoftphone;

public partial class MainWindow : Window
{
    private readonly AppConfig _config;
    private readonly PhoneEngine _engine;
    private readonly MainViewModel _vm;
    private WinForms.NotifyIcon? _tray;
    private bool _reallyExiting;
    private ToastWindow? _toast;

    public MainWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        _engine = new PhoneEngine(config);
        _vm = new MainViewModel(config, _engine, Dispatcher);
        DataContext = _vm;
        _vm.IncomingCallAlert += OnIncomingAlert;

        RestoreWindowState();
        ApplyFlowDirection();
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsRtl)) ApplyFlowDirection();
        };

        Loaded += OnWindowLoaded;
        Closing += OnWindowClosing;
    }

    // ---------- lifecycle ----------

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        Diag.Sink = msg =>
        {
            try { Dispatcher.BeginInvoke(() => _vm.AppendLog(msg)); }
            catch { }
        };

        InitTray();

        if (!_vm.StartEngine())
            _vm.AppendLog("[ui] engine failed to start — see log above", "error");
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveWindowState();

        if (!_reallyExiting)
        {
            // minimize to tray instead of quitting
            e.Cancel = true;
            Hide();
            _tray?.ShowBalloonTip(4000, "SipSoftphone",
                "Still running in the tray — incoming calls keep working.",
                WinForms.ToolTipIcon.Info);
            return;
        }

        _vm.Shutdown();
        _tray?.Dispose();
        _tray = null;
        _engine.Dispose();
        _vm.Dispose();
    }

    public void ExitApplication()
    {
        _reallyExiting = true;
        _toast?.TryClose();
        Close();
    }

    // ---------- tray ----------

    private void InitTray()
    {
        try
        {
            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Show", null, (_, _) => ShowFromTray());
            menu.Items.Add("Exit", null, (_, _) => ExitApplication());

            _tray = new WinForms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Text = "SipSoftphone",
                ContextMenuStrip = menu,
                Visible = true
            };
            _tray.DoubleClick += (_, _) => ShowFromTray();
        }
        catch (Exception ex)
        {
            _vm.AppendLog($"[ui] tray init failed: {ex.Message}", "warn");
        }
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void OnWindowStateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && !_reallyExiting && IsLoaded)
        {
            Hide();
            _tray?.ShowBalloonTip(2000, "SipSoftphone", "Minimized to tray.",
                WinForms.ToolTipIcon.Info);
        }
    }

    // ---------- incoming call toast ----------

    private void OnIncomingAlert(string from, string? name)
    {
        Dispatcher.BeginInvoke(() =>
        {
            FlashTaskbar();
            try
            {
                _toast?.TryClose();
                _toast = new ToastWindow(from, name,
                    () => _ = _engine.AnswerPendingAsync(),
                    () => _engine.RejectPending())
                { Owner = this };
                _toast.Show();
            }
            catch { }
        });
    }

    private void FlashTaskbar()
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

    // ---------- keyboard shortcuts ----------

    private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var call = _engine.CurrentCall.State;

        if (e.Key == Key.F5 && call == CallState.IncomingRinging)
        { _ = _engine.AnswerPendingAsync(); e.Handled = true; return; }

        if (e.Key == Key.F9 && call == CallState.IncomingRinging)
        { _engine.RejectPending(); e.Handled = true; return; }

        if (e.Key == Key.Escape && call is CallState.Active or CallState.OutgoingRinging or CallState.IncomingRinging)
        { _engine.Hangup(); e.Handled = true; return; }

        if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control)
        { DialBox.Focus(); e.Handled = true; return; }

        if (e.Key == Key.M && Keyboard.Modifiers == ModifierKeys.Control && call == CallState.Active)
        { _vm.MuteChecked = !_vm.MuteChecked; e.Handled = true; return; }

        // while a call is active, digit keys send DTMF directly (unless typing in a text box)
        if (call == CallState.Active &&
            e.OriginalSource is not System.Windows.Controls.TextBox &&
            Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            var ch = KeyToDigit(e.Key);
            if (ch != '\0')
            {
                _ = _engine.SendDtmfAsync(ch);
                e.Handled = true;
            }
        }
    }

    private static char KeyToDigit(Key k) => k switch
    {
        >= Key.D0 and <= Key.D9 => (char)('0' + ((int)k - (int)Key.D0)),
        >= Key.NumPad0 and <= Key.NumPad9 => (char)('0' + ((int)k - (int)Key.NumPad0)),
        Key.Multiply => '*',
        Key.Add => '+',
        Key.Subtract => '-',
        Key.Decimal => '.',
        _ => '\0'
    };

    // ---------- RTL ----------

    private void OnRtlClick(object sender, RoutedEventArgs e) => _vm.ToggleRtl();

    private void ApplyFlowDirection()
        => FlowDirection = _vm.IsRtl ? System.Windows.FlowDirection.RightToLeft : System.Windows.FlowDirection.LeftToRight;

    // ---------- window state persistence ----------

    private void RestoreWindowState()
    {
        var ui = _config.Ui;
        if (ui.WindowWidth >= 600 && ui.WindowHeight >= 400)
        {
            Width = ui.WindowWidth;
            Height = ui.WindowHeight;
        }
        if (ui.WindowX is { } x && ui.WindowY is { } y)
        {
            if (x > SystemParameters.VirtualScreenLeft - 200 &&
                x < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
                y > SystemParameters.VirtualScreenTop - 100 &&
                y < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
            {
                Left = x;
                Top = y;
                WindowStartupLocation = WindowStartupLocation.Manual;
            }
        }
        if (ui.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveWindowState()
    {
        try
        {
            if (WindowState == WindowState.Normal)
            {
                _config.Ui.WindowX = Left;
                _config.Ui.WindowY = Top;
                _config.Ui.WindowWidth = Width;
                _config.Ui.WindowHeight = Height;
                _config.Ui.WindowMaximized = false;
            }
            else
            {
                _config.Ui.WindowMaximized = WindowState == WindowState.Maximized;
            }
            _config.Save();
        }
        catch { }
    }

    // ---------- settings ----------

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var before = SnapshotSip();
        var win = new SettingsWindow(_config) { Owner = this };
        if (win.ShowDialog() != true) return;

        var after = SnapshotSip();
        var call = _engine.CurrentCall.State;
        if (call is CallState.Active or CallState.OutgoingRinging or CallState.IncomingRinging)
        {
            _vm.AppendLog("[ui] settings saved — SIP changes apply after the current call", "warn");
        }
        else if (before != after)
        {
            _vm.AppendLog("[ui] SIP settings changed — restarting engine…", "sip");
            _engine.Restart();
            _vm.ServerInfoText = _engine.ServerInfo;
            _vm.ReloadAccountsFromConfig();
        }
        else
        {
            _vm.AppendLog("[ui] settings saved — audio/trigger changes apply on the next call");
        }
    }

    private string SnapshotSip() =>
        $"{_config.Sip.Server}|{_config.Sip.Port}|{_config.Sip.Username}|{_config.Sip.Password}|{_config.Sip.Transport}|{_config.Sip.ExpirySeconds}|{_config.Sip.Accounts.Count}|{_config.Sip.ActiveAccountIndex}";

    // ---------- history / contacts double-click ----------

    private async void OnHistoryDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryItem h && h.Phone.Length > 0)
        {
            _vm.DialText = h.Phone;
            if (_engine.CurrentCall.State is CallState.Idle or CallState.Ended)
                await _engine.CallAsync(h.Phone);
        }
    }

    private async void OnContactDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ContactsList.SelectedItem is ContactItem c && c.Phone.Length > 0)
        {
            _vm.DialText = c.Phone;
            if (_engine.CurrentCall.State is CallState.Idle or CallState.Ended)
                await _engine.CallAsync(c.Phone);
        }
    }

    private void OnDialKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.DialText.Trim().Length > 0 &&
            _engine.CurrentCall.State is CallState.Idle or CallState.Ended)
        {
            _ = _engine.CallAsync(_vm.DialText.Trim());
            e.Handled = true;
        }
    }
}

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
