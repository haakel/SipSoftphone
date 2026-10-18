using System.Text.Json;

namespace SipSoftphone;

public sealed class ContactItem : ObservableObject
{
    private string _name = "";
    private string _phone = "";
    private bool _speedDial;

    public string Name { get => _name; set => Set(ref _name, value); }
    public string Phone { get => _phone; set => Set(ref _phone, value); }
    public bool SpeedDial { get => _speedDial; set => Set(ref _speedDial, value); }

    public string Initials
    {
        get
        {
            var parts = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length switch
            {
                0 => "#",
                1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
                _ => (parts[0][..1] + parts[^1][..1]).ToUpperInvariant()
            };
        }
    }
}

/// <summary>Simple JSON-backed phone book with search + speed dials.</summary>
public sealed class ContactsBook
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _path;

    public List<ContactItem> Contacts { get; private set; } = new();

    public ContactsBook(string? path)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(AppContext.BaseDirectory, "contacts.json")
            : Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
        Load();
    }

    public string Path_ => _path;

    public void Load()
    {
        try
        {
            if (!File.Exists(_path)) { Contacts = SeedDefaults(); Save(); return; }
            Contacts = JsonSerializer.Deserialize<List<ContactItem>>(File.ReadAllText(_path), JsonOpts) ?? new();
        }
        catch
        {
            Contacts = new();
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(Contacts, JsonOpts));
    }

    public IEnumerable<ContactItem> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Contacts;
        var q = query.Trim();
        return Contacts.Where(c =>
            c.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            c.Phone.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    public IEnumerable<ContactItem> SpeedDials(int max = 6)
        => Contacts.Where(c => c.SpeedDial).Take(max);

    public void Add(string name, string phone, bool speedDial = false)
    {
        Contacts.Add(new ContactItem { Name = name.Trim(), Phone = phone.Trim(), SpeedDial = speedDial });
        Save();
    }

    public bool Remove(ContactItem item)
    {
        var removed = Contacts.Remove(item);
        if (removed) Save();
        return removed;
    }

    private static List<ContactItem> SeedDefaults() => new()
    {
        new ContactItem { Name = "Sample Contact", Phone = "9121234567", SpeedDial = true }
    };
}
