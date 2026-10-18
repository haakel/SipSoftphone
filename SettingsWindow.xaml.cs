using System.Windows;
using System.Windows.Controls;
using NAudio.Wave;

namespace SipSoftphone;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private bool _loadingAccounts;
    private RingtonePlayer? _previewPlayer;

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;

        SseEnabledCheck.IsChecked = config.Hook.SseEnabled;
        SsePortBox.Text = config.Hook.SsePort.ToString();
        LogFileBox.Text = config.Hook.LogFile;
        WebhookBox.Text = config.Hook.WebhookUrl;

        TriggerEnabledCheck.IsChecked = config.Trigger.Enabled;
        TriggerOnCombo.Text = config.Trigger.On;
        TriggerUrlBox.Text = config.Trigger.UrlTemplate;
        BrowserPathBox.Text = config.Trigger.BrowserPath;

        try
        {
            MicCombo.ItemsSource = AudioEngine.GetMicDevices();
            SpeakerCombo.ItemsSource = AudioEngine.GetSpeakerDevices();
        }
        catch (Exception ex) { SaveHint.Text = $"audio enum failed: {ex.Message}"; }
        MicCombo.Text = config.Audio.MicDevice;
        SpeakerCombo.Text = config.Audio.SpeakerDevice;
        MicVolumeSlider.Value = Math.Clamp(config.Audio.MicVolume * 100, 0, 100);
        SpeakerVolumeSlider.Value = Math.Clamp(config.Audio.SpeakerVolume * 100, 0, 100);
        RingtoneBox.Text = config.Audio.RingtonePath;

        LoadAccounts();
        Closed += (_, _) => { _previewPlayer?.Dispose(); _previewPlayer = null; };
    }

    // ---------- accounts ----------

    private void LoadAccounts()
    {
        _loadingAccounts = true;
        try
        {
            AccountCombo.ItemsSource = null;
            AccountCombo.ItemsSource = _config.Sip.Accounts;
            var idx = Math.Clamp(_config.Sip.ActiveAccountIndex, 0, Math.Max(0, _config.Sip.Accounts.Count - 1));
            AccountCombo.SelectedIndex = _config.Sip.Accounts.Count == 0 ? -1 : idx;
        }
        finally { _loadingAccounts = false; }
        if (AccountCombo.SelectedIndex < 0) LoadFieldsFromSip();
    }

    private void OnAccountSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingAccounts) return;
        CommitFieldsToCurrentAccount();
        _loadingAccounts = true;
        try
        {
            if (AccountCombo.SelectedItem is SipAccountConfig acc)
            {
                SipServerBox.Text = acc.Server;
                SipPortBox.Text = acc.Port.ToString();
                SipUserBox.Text = acc.Username;
                SipPassBox.Password = acc.Password;
                TransportCombo.Text = string.IsNullOrWhiteSpace(acc.Transport) ? "udp" : acc.Transport;
                ExpiryBox.Text = acc.ExpirySeconds.ToString();
                AccountLabelBox.Text = acc.Label;
            }
        }
        finally { _loadingAccounts = false; }
    }

    private void LoadFieldsFromSip()
    {
        SipServerBox.Text = _config.Sip.Server;
        SipPortBox.Text = _config.Sip.Port.ToString();
        SipUserBox.Text = _config.Sip.Username;
        SipPassBox.Password = _config.Sip.Password;
        TransportCombo.Text = string.IsNullOrWhiteSpace(_config.Sip.Transport) ? "udp" : _config.Sip.Transport;
        ExpiryBox.Text = _config.Sip.ExpirySeconds.ToString();
        AccountLabelBox.Text = $"{_config.Sip.Username}@{_config.Sip.Server}";
    }

    private bool TryReadSipFields(out string server, out int port, out string user, out string pass,
        out string transport, out int expiry)
    {
        server = SipServerBox.Text.Trim();
        user = SipUserBox.Text.Trim();
        pass = SipPassBox.Password;
        transport = (TransportCombo.Text ?? "udp").Trim().ToLowerInvariant();
        if (transport is not ("udp" or "tcp" or "tls")) transport = "udp";
        port = 0; expiry = 0;
        if (!int.TryParse(SipPortBox.Text.Trim(), out port) || port is < 1 or > 65535)
        { SaveHint.Text = "invalid SIP port"; return false; }
        if (!int.TryParse(ExpiryBox.Text.Trim(), out expiry) || expiry < 30)
        { SaveHint.Text = "invalid expiry (min 30)"; return false; }
        return true;
    }

    private void CommitFieldsToCurrentAccount()
    {
        if (!TryReadSipFields(out var server, out var port, out var user, out var pass, out var transport, out var expiry))
            return;

        if (AccountCombo.SelectedItem is SipAccountConfig acc)
        {
            acc.Server = server;
            acc.Port = port;
            acc.Username = user;
            acc.Password = pass;
            acc.Transport = transport;
            acc.ExpirySeconds = expiry;
            acc.Label = string.IsNullOrWhiteSpace(AccountLabelBox.Text.Trim())
                ? $"{user}@{server}" : AccountLabelBox.Text.Trim();
        }
    }

    private void OnAddAccount(object sender, RoutedEventArgs e)
    {
        CommitFieldsToCurrentAccount();
        var last = _config.Sip.Accounts.LastOrDefault();
        var copy = new SipAccountConfig
        {
            Label = "new account",
            Server = last?.Server ?? "",
            Port = last?.Port ?? 5060,
            Username = "",
            Password = "",
            Transport = last?.Transport ?? "udp",
            ExpirySeconds = last?.ExpirySeconds ?? 1800
        };
        _config.Sip.Accounts.Add(copy);
        _loadingAccounts = true;
        try { AccountCombo.SelectedIndex = _config.Sip.Accounts.Count - 1; }
        finally { _loadingAccounts = false; }
        AccountCombo.ItemsSource = null;
        AccountCombo.ItemsSource = _config.Sip.Accounts;
        AccountCombo.SelectedIndex = _config.Sip.Accounts.Count - 1;
        SipUserBox.Text = "";
        SipPassBox.Password = "";
        AccountLabelBox.Text = "new account";
        SipUserBox.Focus();
    }

    private void OnRemoveAccount(object sender, RoutedEventArgs e)
    {
        if (AccountCombo.SelectedItem is not SipAccountConfig acc) return;
        _config.Sip.Accounts.Remove(acc);
        _loadingAccounts = true;
        try
        {
            AccountCombo.ItemsSource = null;
            AccountCombo.ItemsSource = _config.Sip.Accounts;
            AccountCombo.SelectedIndex = _config.Sip.Accounts.Count == 0 ? -1 : 0;
        }
        finally { _loadingAccounts = false; }
        if (AccountCombo.SelectedIndex < 0) LoadFieldsFromSip();
    }

    // ---------- tests ----------

    private async void OnTestSipClick(object sender, RoutedEventArgs e)
    {
        if (!TryReadSipFields(out var server, out var port, out var user, out _, out var transport, out _)) return;
        if (server.Length == 0) { TestSipResult.Text = "no server"; return; }

        TestSipButton.IsEnabled = false;
        TestSipResult.Text = "testing…";
        try
        {
            var result = await PhoneEngine.TestServerAsync(server, port, transport, user);
            TestSipResult.Text = result;
        }
        finally { TestSipButton.IsEnabled = true; }
    }

    private void OnTestSpeakerClick(object sender, RoutedEventArgs e)
    {
        try
        {
            const int rate = 8000;
            var sampleCount = rate / 2; // 500 ms
            var samples = new short[sampleCount];
            for (int i = 0; i < sampleCount; i++)
                samples[i] = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 8000);
            var bytes = new byte[sampleCount * 2];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);

            var provider = new BufferedWaveProvider(new WaveFormat(rate, 16, 1)) { ReadFully = true };
            provider.AddSamples(bytes, 0, bytes.Length);
            var wo = new WaveOut { DeviceNumber = -1, Volume = (float)Math.Clamp(SpeakerVolumeSlider.Value / 100, 0, 1) };
            wo.Init(provider);
            wo.Play();
            var t = new System.Threading.Timer(_ => { try { wo.Stop(); wo.Dispose(); } catch { } }, null, 900, Timeout.Infinite);
            SaveHint.Text = "playing test tone…";
        }
        catch (Exception ex) { SaveHint.Text = $"speaker test failed: {ex.Message}"; }
    }

    private void OnTestMicClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var device = MicCombo.Text;
            var devIdx = -1;
            for (int i = 0; i < WaveIn.DeviceCount; i++)
                if (!string.IsNullOrWhiteSpace(device) && WaveIn.GetCapabilities(i).ProductName.Contains(device, StringComparison.OrdinalIgnoreCase))
                { devIdx = i; break; }

            short peak = 0;
            var done = new ManualResetEventSlim();
            var mic = new WaveIn { DeviceNumber = devIdx, WaveFormat = new WaveFormat(8000, 16, 1), BufferMilliseconds = 50 };
            mic.DataAvailable += (_, args) =>
            {
                for (int i = 0; i + 1 < args.BytesRecorded; i += 2)
                {
                    var s = (short)(args.Buffer[i] | (args.Buffer[i + 1] << 8));
                    var abs = Math.Abs(s);
                    if (abs > peak) peak = abs;
                }
            };
            mic.RecordingStopped += (_, _) => done.Set();
            mic.StartRecording();
            SaveHint.Text = "mic test: say something…";
            System.Threading.Tasks.Task.Run(() =>
            {
                done.Wait(1200);
                try { mic.StopRecording(); mic.Dispose(); } catch { }
                var pct = Math.Min(100, peak * 100 / short.MaxValue);
                Dispatcher.BeginInvoke(() => SaveHint.Text = pct < 2 ? "mic test: no signal detected" : $"mic test: peak {pct}%");
            });
        }
        catch (Exception ex) { SaveHint.Text = $"mic test failed: {ex.Message}"; }
    }

    // ---------- ringtone ----------

    private void OnBrowseRingtone(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "WAV files (*.wav)|*.wav|All files (*.*)|*.*",
            Title = "Choose ringtone"
        };
        if (dlg.ShowDialog() == true) RingtoneBox.Text = dlg.FileName;
    }

    private void OnTestRingtone(object sender, RoutedEventArgs e)
    {
        _previewPlayer?.Dispose();
        _previewPlayer = new RingtonePlayer();
        _previewPlayer.Play(RingtoneBox.Text.Trim());
        SaveHint.Text = "playing ringtone preview (3s)…";
        var t = new System.Threading.Timer(_ => Dispatcher.BeginInvoke(() => _previewPlayer?.Stop()), null, 3000, Timeout.Infinite);
    }

    private void OnVolumeValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MicVolumeText != null) MicVolumeText.Text = $"{MicVolumeSlider.Value:0}%";
        if (SpeakerVolumeText != null) SpeakerVolumeText.Text = $"{SpeakerVolumeSlider.Value:0}%";
    }

    // ---------- save / cancel ----------

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SsePortBox.Text.Trim(), out var ssePort) || ssePort is < 1 or > 65535)
        { SaveHint.Text = "invalid SSE port"; return; }
        if (!TryReadSipFields(out var server, out var port, out var user, out var pass, out var transport, out var expiry))
            return;

        CommitFieldsToCurrentAccount();

        // ensure at least one account exists
        if (_config.Sip.Accounts.Count == 0)
        {
            _config.Sip.Accounts.Add(new SipAccountConfig
            {
                Label = $"{user}@{server}",
                Server = server, Port = port, Username = user, Password = pass,
                Transport = transport, ExpirySeconds = expiry
            });
        }

        var activeIdx = AccountCombo.SelectedIndex >= 0 ? AccountCombo.SelectedIndex : 0;
        if (activeIdx >= _config.Sip.Accounts.Count) activeIdx = 0;
        _config.Sip.ActiveAccountIndex = activeIdx;
        _config.Sip.Accounts[activeIdx].CopyTo(_config.Sip);

        _config.Hook.SseEnabled = SseEnabledCheck.IsChecked == true;
        _config.Hook.SsePort = ssePort;
        _config.Hook.LogFile = LogFileBox.Text.Trim();
        _config.Hook.WebhookUrl = WebhookBox.Text.Trim();

        _config.Trigger.Enabled = TriggerEnabledCheck.IsChecked == true;
        _config.Trigger.On = TriggerOnCombo.Text.Trim();
        _config.Trigger.UrlTemplate = TriggerUrlBox.Text.Trim();
        _config.Trigger.BrowserPath = BrowserPathBox.Text.Trim();

        _config.Audio.MicDevice = MicCombo.Text.Trim();
        _config.Audio.SpeakerDevice = SpeakerCombo.Text.Trim();
        _config.Audio.MicVolume = Math.Clamp(MicVolumeSlider.Value / 100, 0, 1);
        _config.Audio.SpeakerVolume = Math.Clamp(SpeakerVolumeSlider.Value / 100, 0, 1);
        _config.Audio.RingtonePath = RingtoneBox.Text.Trim();

        try
        {
            _config.Save();
            DialogResult = true;
        }
        catch (Exception ex)
        {
            SaveHint.Text = $"save failed: {ex.Message}";
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
