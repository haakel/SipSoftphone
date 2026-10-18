using System.Text.Json;

namespace SipSoftphone;

public class AppConfig
{
    public SipConfig Sip { get; set; } = new();
    public HookConfig Hook { get; set; } = new();
    public TriggerConfig Trigger { get; set; } = new();
    public AudioConfig Audio { get; set; } = new();
    public UiConfig Ui { get; set; } = new();
    public string ContactsFile { get; set; } = "contacts.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    public static AppConfig Load(string? path)
    {
        var file = string.IsNullOrWhiteSpace(path) ? DefaultPath : Path.GetFullPath(path);
        if (!File.Exists(file))
        {
            var def = new AppConfig();
            File.WriteAllText(file, JsonSerializer.Serialize(def, JsonOpts));
            Console.WriteLine($"[config] no config found, wrote defaults to {file}");
            return def;
        }
        var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(file), JsonOpts) ?? new AppConfig();
        cfg.ConfigPath = file;
        cfg.Normalize();
        return cfg;
    }

    /// <summary>Migrates legacy single-account config and keeps Accounts/Sip fields in sync.</summary>
    public void Normalize()
    {
        if (Sip.Accounts.Count == 0 && !string.IsNullOrWhiteSpace(Sip.Username))
            Sip.Accounts.Add(SipConfig.AccountFrom(Sip));

        if (Sip.ActiveAccountIndex < 0 || Sip.ActiveAccountIndex >= Sip.Accounts.Count)
            Sip.ActiveAccountIndex = 0;

        if (Sip.Accounts.Count > 0)
            Sip.Accounts[Sip.ActiveAccountIndex].CopyTo(Sip);
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string ConfigPath { get; set; } = DefaultPath;

    public void Save(string? path = null)
    {
        var file = string.IsNullOrWhiteSpace(path) ? ConfigPath : Path.GetFullPath(path);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
        File.Move(tmp, file, true);
        ConfigPath = file;
    }
}

public class SipConfig
{
    public string Server { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public int Port { get; set; } = 5060;
    public int ExpirySeconds { get; set; } = 1800;
    public bool AutoAnswer { get; set; } = false;
    public bool Trace { get; set; } = false;
    /// <summary>udp | tcp | tls</summary>
    public string Transport { get; set; } = "udp";
    public int ActiveAccountIndex { get; set; } = 0;
    public List<SipAccountConfig> Accounts { get; set; } = new();

    public static SipAccountConfig AccountFrom(SipConfig sip) => new()
    {
        Label = $"{sip.Username}@{sip.Server}",
        Server = sip.Server,
        Port = sip.Port,
        Username = sip.Username,
        Password = sip.Password,
        Transport = sip.Transport,
        ExpirySeconds = sip.ExpirySeconds
    };
}

public class SipAccountConfig
{
    public string Label { get; set; } = "";
    public string Server { get; set; } = "";
    public int Port { get; set; } = 5060;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Transport { get; set; } = "udp";
    public int ExpirySeconds { get; set; } = 1800;

    public void CopyTo(SipConfig sip)
    {
        sip.Server = Server;
        sip.Port = Port;
        sip.Username = Username;
        sip.Password = Password;
        if (!string.IsNullOrWhiteSpace(Transport)) sip.Transport = Transport;
        if (ExpirySeconds >= 30) sip.ExpirySeconds = ExpirySeconds;
    }

    public override string ToString() => string.IsNullOrWhiteSpace(Label)
        ? $"{Username}@{Server}:{Port}"
        : Label;
}

public class HookConfig
{
    public bool SseEnabled { get; set; } = true;
    public int SsePort { get; set; } = 8787;
    public string LogFile { get; set; } = "logs/calls.jsonl";
    public string WebhookUrl { get; set; } = "";
}

public class TriggerConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>call_answered | call_received | registration | off</summary>
    public string On { get; set; } = "call_answered";
    public string UrlTemplate { get; set; } = "https://login.melipayamak.com/Reseller/?module=users#call={phone}";
    public string BrowserPath { get; set; } = "";
}

public class AudioConfig
{
    /// <summary>empty = Windows default device; otherwise substring of the device name</summary>
    public string MicDevice { get; set; } = "";
    public string SpeakerDevice { get; set; } = "";
    /// <summary>Optional path to a .wav ringtone; empty = Windows default sound.</summary>
    public string RingtonePath { get; set; } = "";
    /// <summary>0..1</summary>
    public double MicVolume { get; set; } = 1.0;
    /// <summary>0..1</summary>
    public double SpeakerVolume { get; set; } = 1.0;
}

public class UiConfig
{
    public bool Rtl { get; set; } = false;
    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    public double WindowWidth { get; set; } = 1080;
    public double WindowHeight { get; set; } = 720;
    public bool WindowMaximized { get; set; } = false;
}
