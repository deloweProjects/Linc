using System.Security.Cryptography;
using System.Text.Json;

namespace Linc.Desktop.Services;

/// <summary>
/// The 32-byte link secret this PC shares with each phone (v32, linclink.md §2), keyed by the
/// phone's LincLink id (the hash of its certificate). It keys the UDP beacon, so only the pair can
/// make or read each other's "I'm here". Made here, sent to the phone once over an authenticated
/// session (<c>link.secret</c>), replaced (gen + 1) only when forgotten.
///
/// Kept in its own file beside <c>tls-identity.pfx</c>, with the same protection (the user's
/// profile), and never in settings.json or a log.
/// </summary>
public sealed class LinkSecretStore(string rootPath)
{
    private sealed class Entry
    {
        public string Secret { get; set; } = "";
        public int Gen { get; set; }
    }

    private readonly object _lock = new();
    private readonly string _path = Path.Combine(rootPath, "link-secrets.json");
    private Dictionary<string, Entry>? _entries;

    /// <summary>The secret and its generation for this phone, making one the first time.</summary>
    public (byte[] Secret, int Gen) Ensure(string phoneId)
    {
        lock (_lock)
        {
            var entries = Load();
            if (!entries.TryGetValue(phoneId, out var entry) || Decode(entry.Secret) is null)
            {
                entry = new Entry { Secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), Gen = (entry?.Gen ?? 0) + 1 };
                entries[phoneId] = entry;
                Save(entries);
            }
            return (Decode(entry.Secret)!, entry.Gen);
        }
    }

    /// <summary>Every phone's secret, for matching a beacon from whichever phone sent it.</summary>
    public IReadOnlyList<(string PhoneId, byte[] Secret)> All()
    {
        lock (_lock)
        {
            return Load().Select(e => (e.Key, Decode(e.Value.Secret))).Where(e => e.Item2 is not null)
                .Select(e => (e.Key, e.Item2!)).ToList();
        }
    }

    public byte[]? Get(string phoneId)
    {
        lock (_lock)
        {
            return Load().TryGetValue(phoneId, out var entry) ? Decode(entry.Secret) : null;
        }
    }

    public void Forget(string phoneId)
    {
        lock (_lock)
        {
            if (Load().Remove(phoneId))
            {
                Save(_entries!);
            }
        }
    }

    /// <summary>The id the phone uses for itself in v32: the first 16 hex digits of SHA-256(cert DER), as for the PC.</summary>
    public static string? PhoneIdOf(string? certBase64)
    {
        if (string.IsNullOrEmpty(certBase64))
        {
            return null;
        }
        try
        {
            return Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(certBase64)))[..16].ToLowerInvariant();
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private Dictionary<string, Entry> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }
        try
        {
            _entries = File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _entries = []; // unreadable: new secrets are made and re-shared, nothing else breaks
        }
        return _entries;
    }

    private void Save(Dictionary<string, Entry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entries));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In memory only until the next save; the phone is re-sent a secret next time.
        }
    }

    private static byte[]? Decode(string text)
    {
        try
        {
            var bytes = Convert.FromBase64String(text);
            return bytes.Length == 32 ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
