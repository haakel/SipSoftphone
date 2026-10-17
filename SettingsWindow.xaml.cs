using System.Windows;

namespace SipSoftphone;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;

        SipServerBox.Text = config.Sip.Server;
        SipUserBox.Text = config.Sip.Username;
        SipPassBox.Password = config.Sip.Password;
        SipPortBox.Text = config.Sip.Port.ToString();
        ExpiryBox.Text = config.Sip.ExpirySeconds.ToString();
        AutoAnswerCheck.IsChecked = config.Sip.AutoAnswer;
        TraceCheck.IsChecked = config.Sip.Trace;

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
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SipPortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        { SaveHint.Text = "invalid SIP port"; return; }
        if (!int.TryParse(SsePortBox.Text.Trim(), out var ssePort) || ssePort is < 1 or > 65535)
        { SaveHint.Text = "invalid SSE port"; return; }
        if (!int.TryParse(ExpiryBox.Text.Trim(), out var expiry) || expiry < 30)
        { SaveHint.Text = "invalid expiry (min 30)"; return; }

        _config.Sip.Server = SipServerBox.Text.Trim();
        _config.Sip.Username = SipUserBox.Text.Trim();
        _config.Sip.Password = SipPassBox.Password;
        _config.Sip.Port = port;
        _config.Sip.ExpirySeconds = expiry;
        _config.Sip.AutoAnswer = AutoAnswerCheck.IsChecked == true;
        _config.Sip.Trace = TraceCheck.IsChecked == true;

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
