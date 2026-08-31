using System.IO;
using System.Text.Json;

namespace Linc.Desktop.Services;

/// <summary>What was playing on the phone — metadata only, never the art itself.</summary>
public sealed record CachedMedia(string? Title, string? Artist, bool Playing, string? AppPackage);

/// <summary>A remembered photo from the Home strip; the thumbnail sits beside it on disk.</summary>
public sealed record CachedPhoto(string Id, string Path);

/// <summary>Last-known device state, restored when the phone is away (M02, D-032).</summary>
public sealed record CachedDevice(
    string Serial,
    string Model,
    DateTimeOffset LastSeenUtc,
    DeviceStatus? Status,
    string? WallpaperId,
    CachedMedia? Media = null,
    IReadOnlyList<CachedPhoto>? Photos = null);

public interface IDeviceCacheService
{
    /// <summary>Cached state for the paired device, or null when nothing has been stored yet.</summary>
    CachedDevice? Current { get; }

    /// <summary>Raised when the cache is loaded or replaced, so surfaces can re-render.</summary>
    event Action? Changed;

    void Save(string serial, string model, DeviceStatus status);

    /// <summary>Remembers what was playing (metadata only — album art is not persisted).</summary>
    void SaveMedia(string serial, CachedMedia? media);

    /// <summary>Remembers the Home photo strip, writing each thumbnail beside the state file.</summary>
    void SavePhoto(string serial, string photoId, string path, byte[] thumbnail);

    /// <summary>Cached thumbnail bytes for a remembered photo, or null.</summary>
    byte[]? LoadPhotoThumbnail(string serial, string photoId);

    /// <summary>Cached wallpaper bytes for the paired device, or null.</summary>
    byte[]? LoadWallpaper(string serial);
    void SaveWallpaper(string serial, string wallpaperId, byte[] bytes);

    /// <summary>
    /// When a device — any device, not just the active one — was last seen, read from its own
    /// cache folder. Backs the tab strip's presence line for phones that aren't loaded (M03).
    /// </summary>
    DateTimeOffset? LastSeen(string serial);

    /// <summary>Forgets everything cached for a device (the Settings action).</summary>
    void Clear(string serial);
}

/// <summary>
/// Persists the innocuous half of a device's state under %LOCALAPPDATA%\Linc/cache\&lt;serial&gt;\
/// so a disconnect never blanks the UI (D-032).
///
/// Privacy rule, deliberately enforced by what this type can hold: stats, palette, wallpaper and
/// the "last seen" stamp persist; clipboard text and notification bodies do not, and there is no
/// field here to put them in. Anything sensitive stays in memory for the session exactly as
/// before — a future opt-in would have to change this type on purpose, not by accident.
/// </summary>
public sealed class DeviceCacheService : IDeviceCacheService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private readonly IDeviceRegistry registry;
    private readonly ILogService log;
    private CachedDevice? _current;
    private DateTimeOffset _lastWrite = DateTimeOffset.MinValue;

    public DeviceCacheService(IDeviceRegistry registry, ILogService log)
    {
        this.registry = registry;
        this.log = log;

        // Switching tabs must show the new phone's copy, never the old one's (M03, D-037).
        // Dropping the loaded state makes the next Current read it from that serial's folder.
        registry.ActiveDeviceChanged += () =>
        {
            _current = null;
            _lastWrite = DateTimeOffset.MinValue;
            Changed?.Invoke();
        };
    }

    public event Action? Changed;

    public CachedDevice? Current
    {
        get
        {
            // Never hand back another phone's copy: connecting to a different device changes
            // PairedSerial without the tab-switch event (see DeviceRegistry.SavePairedDevice).
            if (_current is not null && _current.Serial != registry.PairedSerial)
            {
                _current = null;
            }
            if (_current is null && registry.PairedSerial is { } serial)
            {
                _current = Load(serial);
            }
            return _current;
        }
    }

    public void Save(string serial, string model, DeviceStatus status)
    {
        _current = new CachedDevice(serial, model, DateTimeOffset.UtcNow, status, status.WallpaperId);
        Changed?.Invoke();

        Flush(serial, force: false);
    }

    /// <summary>
    /// Writes the state file. Throttled because status arrives on the health-loop cadence and
    /// losing up to a minute of it costs nothing; <paramref name="force"/> bypasses that.
    /// </summary>
    private void Flush(string serial, bool force)
    {
        if (!force && DateTimeOffset.UtcNow - _lastWrite < TimeSpan.FromSeconds(60))
        {
            return;
        }
        _lastWrite = DateTimeOffset.UtcNow;
        try
        {
            var dir = DirectoryFor(serial);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "state.json"), JsonSerializer.Serialize(_current, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log.Log(LogLevel.Warn, $"Couldn't save this phone's offline copy: {ex.Message}");
        }
    }

    private static string SafeName(string id) =>
        string.Join("_", id.Split(Path.GetInvalidFileNameChars()));

    public void SaveMedia(string serial, CachedMedia? media)
    {
        if (_current is null)
        {
            return; // nothing to attach it to until a status has landed
        }
        _current = _current with { Media = media };
        Changed?.Invoke();
        Flush(serial, force: false);
    }

    public void SavePhoto(string serial, string photoId, string path, byte[] thumbnail)
    {
        try
        {
            var dir = Path.Combine(DirectoryFor(serial), "photos");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, SafeName(photoId)), thumbnail);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // a missing thumbnail just means that tile renders empty
        }

        if (_current is null)
        {
            return;
        }
        var photos = (_current.Photos ?? []).Where(p => p.Id != photoId).ToList();
        photos.Insert(0, new CachedPhoto(photoId, path));
        _current = _current with { Photos = photos.Take(20).ToList() };
        Flush(serial, force: false);
    }

    public byte[]? LoadPhotoThumbnail(string serial, string photoId)
    {
        try
        {
            var file = Path.Combine(DirectoryFor(serial), "photos", SafeName(photoId));
            return File.Exists(file) ? File.ReadAllBytes(file) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public byte[]? LoadWallpaper(string serial)
    {
        try
        {
            var path = Path.Combine(DirectoryFor(serial), "wallpaper.bin");
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void SaveWallpaper(string serial, string wallpaperId, byte[] bytes)
    {
        try
        {
            var dir = DirectoryFor(serial);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "wallpaper.bin"), bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Log(LogLevel.Warn, $"Couldn't save this phone's wallpaper copy: {ex.Message}");
        }
    }

    public DateTimeOffset? LastSeen(string serial) =>
        serial == registry.PairedSerial ? Current?.LastSeenUtc : Load(serial)?.LastSeenUtc;

    public void Clear(string serial)
    {
        _current = null;
        try
        {
            var dir = DirectoryFor(serial);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
            log.Log(LogLevel.Info, "Cleared this phone's offline copy.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Log(LogLevel.Warn, $"Couldn't clear this phone's offline copy: {ex.Message}");
        }
        Changed?.Invoke();
    }

    private CachedDevice? Load(string serial)
    {
        try
        {
            var path = Path.Combine(DirectoryFor(serial), "state.json");
            return File.Exists(path)
                ? JsonSerializer.Deserialize<CachedDevice>(File.ReadAllText(path))
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null; // a corrupt cache is just an empty one
        }
    }

    // Serials can contain ':' on wireless links, which is not a legal path character.
    private string DirectoryFor(string serial) =>
        Path.Combine(registry.RootPath, "cache", string.Join("_", serial.Split(Path.GetInvalidFileNameChars())));
}
