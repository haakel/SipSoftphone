using System.Diagnostics;

namespace SipSoftphone;

/// <summary>
/// Opens the Melipayamak panel URL with #call=&lt;phone&gt; so the
/// "Melipayamak Auto Search" extension picks it up and auto-searches.
/// Works both when Chrome is closed (starts it) and when it is already
/// running (new tab in existing window).
/// </summary>
public static class ChromeTrigger
{
    private static readonly object _dedupeLock = new();
    private static readonly Dictionary<string, DateTime> _recent = new();
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(5);

    public static void Fire(TriggerConfig cfg, string rawPhone, string evt)
    {
        if (!cfg.Enabled || string.Equals(cfg.On, "off", StringComparison.OrdinalIgnoreCase)) return;
        if (!string.Equals(evt, cfg.On, StringComparison.OrdinalIgnoreCase)) return;

        var phone = PhoneNormalizer.Normalize(rawPhone);
        if (string.IsNullOrEmpty(phone)) return;

        var url = cfg.UrlTemplate.Replace("{phone}", phone);

        lock (_dedupeLock)
        {
            var key = evt + "|" + phone;
            var now = DateTime.UtcNow;
            foreach (var k in _recent.Where(kv => now - kv.Value > DedupeWindow).Select(kv => kv.Key).ToList())
                _recent.Remove(k);
            if (_recent.TryGetValue(key, out var last) && now - last < DedupeWindow)
            {
                Diag.WriteLine($"[trigger] skipped duplicate {key}");
                return;
            }
            _recent[key] = now;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = string.IsNullOrWhiteSpace(cfg.BrowserPath) ? url : cfg.BrowserPath,
                Arguments = string.IsNullOrWhiteSpace(cfg.BrowserPath) ? "" : $"\"{url}\"",
                UseShellExecute = true
            };
            Process.Start(psi);
            Diag.WriteLine($"[trigger] opened {url}");
        }
        catch (Exception ex)
        {
            Diag.WriteLine($"[trigger] failed: {ex.Message}");
        }
    }
}
