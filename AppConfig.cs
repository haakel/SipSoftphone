using System.Text.Json;

namespace SipSoftphone;

public class AppConfig
{
    public SipConfig Sip { get; set; } = new();
    public HookConfig Hook { get; set; } = new();
    public TriggerConfig Trigger { get; set; } = new();
    public AudioConfig Audio { get; set; } = new();

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
        return cfg;
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
}
