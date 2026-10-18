using SipSoftphone;
using Xunit;

namespace SipSoftphone.Tests;

public class ContactsBookTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public ContactsBookTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sipsoftphone-contacts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "contacts.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void New_book_seeds_default_contact()
    {
        var book = new ContactsBook(_path);
        Assert.NotEmpty(book.Contacts);
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void Add_save_load_roundtrip()
    {
        var book = new ContactsBook(_path);
        book.Add("Sara", "9129998877", speedDial: true);

        var reloaded = new ContactsBook(_path);
        Assert.Contains(reloaded.Contacts, c => c.Name == "Sara" && c.Phone == "9129998877" && c.SpeedDial);
    }

    [Fact]
    public void Search_matches_name_and_phone()
    {
        var book = new ContactsBook(_path);
        book.Add("Reza Ahmadi", "9351112233", false);

        Assert.Contains(book.Search("reza"), c => c.Name == "Reza Ahmadi");
        Assert.Contains(book.Search("935"), c => c.Phone == "9351112233");
        Assert.Empty(book.Search("nomatch-xyz"));
    }

    [Fact]
    public void Remove_deletes_and_persists()
    {
        var book = new ContactsBook(_path);
        var c = new ContactItem { Name = "Temp", Phone = "123" };
        book.Contacts.Add(c);
        Assert.True(book.Remove(c));

        var reloaded = new ContactsBook(_path);
        Assert.DoesNotContain(reloaded.Contacts, x => x.Name == "Temp");
    }

    [Fact]
    public void SpeedDials_returns_marked_contacts_only()
    {
        var book = new ContactsBook(_path);
        book.Contacts.Clear();
        book.Add("A", "111", true);
        book.Add("B", "222", false);
        book.Add("C", "333", true);

        var sd = book.SpeedDials(6).ToList();
        Assert.Equal(2, sd.Count);
        Assert.All(sd, c => Assert.True(c.SpeedDial));
    }
}

public class AppConfigTests : IDisposable
{
    private readonly string _dir;

    public AppConfigTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sipsoftphone-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Load_missing_file_writes_defaults()
    {
        var path = Path.Combine(_dir, "config.json");
        var cfg = AppConfig.Load(path);
        Assert.True(File.Exists(path));
        Assert.Equal("udp", cfg.Sip.Transport);
        Assert.NotNull(cfg.Ui);
    }

    [Fact]
    public void Normalize_migrates_legacy_single_account()
    {
        var cfg = new AppConfig();
        cfg.Sip.Server = "pbx.local";
        cfg.Sip.Username = "100";
        cfg.Sip.Password = "secret";
        cfg.Sip.Port = 5060;

        cfg.Normalize();

        Assert.Single(cfg.Sip.Accounts);
        Assert.Equal("100", cfg.Sip.Accounts[0].Username);
        Assert.Equal(0, cfg.Sip.ActiveAccountIndex);
    }

    [Fact]
    public void Save_load_roundtrip_preserves_new_fields()
    {
        var path = Path.Combine(_dir, "config.json");
        var cfg = new AppConfig();
        cfg.Sip.Transport = "tcp";
        cfg.Audio.RingtonePath = "ring.wav";
        cfg.Audio.MicVolume = 0.5;
        cfg.Ui.Rtl = true;
        cfg.Ui.WindowX = 10;
        cfg.Ui.WindowY = 20;
        cfg.ContactsFile = "my-contacts.json";
        cfg.Save(path);

        var loaded = AppConfig.Load(path);
        Assert.Equal("tcp", loaded.Sip.Transport);
        Assert.Equal("ring.wav", loaded.Audio.RingtonePath);
        Assert.Equal(0.5, loaded.Audio.MicVolume);
        Assert.True(loaded.Ui.Rtl);
        Assert.Equal(10, loaded.Ui.WindowX);
        Assert.Equal(20, loaded.Ui.WindowY);
        Assert.Equal("my-contacts.json", loaded.ContactsFile);
    }
}
