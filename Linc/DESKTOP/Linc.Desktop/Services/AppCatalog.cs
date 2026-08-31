using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Linc.Desktop.Services;

/// <summary>What a refresh changed, for the log line and the caller's "anything new?" check.</summary>
public sealed record AppReconcileResult(int Added, int Removed, int Relabelled, int Total)
{
    public bool AnyChange => Added > 0 || Removed > 0 || Relabelled > 0;

    public override string ToString() =>
        $"{Total} app(s): {Added} added, {Removed} removed, {Relabelled} relabelled";
}

/// <summary>
/// The per-device installed-app cache (M6c, D-058). One catalog per phone serial:
/// <c>&lt;root&gt;\cache\&lt;serial&gt;\apps.json</c> for the list and
/// <c>&lt;root&gt;\cache\&lt;serial&gt;\icons\&lt;package&gt;.png</c> for the icons.
///
/// <para><b>The root is injected, never resolved here.</b> It comes from
/// <see cref="DeviceRegistry.RootPath"/>, so a harness pointed at a temp root cannot reach the
/// owner's real <c>cache\</c> any more than it can reach their real <c>settings.json</c>
/// (D-057/D-058). Calling <see cref="Environment.GetFolderPath"/> in here would quietly reopen
/// exactly the hole D-057 closed.</para>
///
/// <para><b>Load is synchronous and instant; refresh is a background pull.</b> Home shows the
/// cached list the moment the page appears and reconciles when the phone answers — the desktop
/// re-requests on connect because there is deliberately no install/remove push event.</para>
///
/// <para>Nothing in here throws for a bad cache. A corrupt, truncated or hostile
/// <c>apps.json</c> degrades to "empty cache, refresh on connect": the alternative is a startup
/// crash caused by a file the product can regenerate for free.</para>
/// </summary>
public sealed class AppCatalog
{
    private const string ListFileName = "apps.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _root;
    private readonly ILogService? _log;

    /// <param name="rootPath">
    /// The Linc store directory — pass <see cref="DeviceRegistry.RootPath"/>. Production gets the
    /// owner's real store because the registry resolved it; a harness gets its temp root for the
    /// same reason.
    /// </param>
    public AppCatalog(string rootPath, ILogService? log = null)
    {
        _root = rootPath;
        _log = log;
    }

    /// <summary>Convenience overload: take the root from the registry, which is the rule (D-058).</summary>
    public AppCatalog(DeviceRegistry registry, ILogService? log = null)
        : this(registry.RootPath, log)
    {
    }

    /// <summary>The cache directory for one phone. Not created until something is written.</summary>
    public string DirectoryFor(string serial) => Path.Combine(_root, "cache", Sanitize(serial));

    /// <summary>The apps.json path for one phone.</summary>
    public string ListPathFor(string serial) => Path.Combine(DirectoryFor(serial), ListFileName);

    /// <summary>The cached PNG path for one app's icon.</summary>
    public string IconPathFor(string serial, string package) =>
        Path.Combine(DirectoryFor(serial), "icons", Sanitize(package) + ".png");

    /// <summary>
    /// The cached list for a phone, or an empty list if there is no cache, it is unreadable, or
    /// its contents are not a well-formed app list. Synchronous and instant by design — Home
    /// calls it on the UI thread while the refresh runs behind it.
    /// </summary>
    public IReadOnlyList<AppInfo> Load(string serial)
    {
        var path = ListPathFor(serial);
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }
            var cached = JsonSerializer.Deserialize<CachedApp[]>(File.ReadAllText(path));
            if (cached is null)
            {
                return [];
            }
            // Same tolerance as the wire parser: an entry we cannot name honestly is skipped
            // rather than shown as its package id (PROTOCOL.md v16).
            return cached
                .Where(c => !string.IsNullOrWhiteSpace(c.Package) && !string.IsNullOrWhiteSpace(c.Label))
                .Select(c => new AppInfo(
                    c.Package!,
                    c.Label!,
                    string.IsNullOrWhiteSpace(c.VersionName) ? null : c.VersionName,
                    c.System))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A cache we cannot read is not an error the owner can act on — it costs one refresh.
            _log?.Log(LogLevel.Warn, $"Apps: cached list for {serial} was unreadable ({ex.GetType().Name}); starting empty.");
            return [];
        }
    }

    /// <summary>
    /// Persists the list for a phone. Returns false (and logs) rather than throwing if the write
    /// fails — a cache miss next launch is not worth taking the app down for.
    /// </summary>
    public bool Save(string serial, IReadOnlyList<AppInfo> apps)
    {
        var path = ListPathFor(serial);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var payload = apps
                .Select(a => new CachedApp
                {
                    Package = a.Package,
                    Label = a.Label,
                    VersionName = a.VersionName,
                    System = a.IsSystem,
                })
                .ToArray();
            File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Log(LogLevel.Warn, $"Apps: could not write the cached list for {serial}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Reconciles a fresh reply against the cache and persists it — <b>diff, don't replace</b>, so
    /// the log says what actually changed on the phone rather than "wrote 214 apps" every connect.
    /// Returns the counts; the new list is what is stored.
    /// </summary>
    public AppReconcileResult Reconcile(string serial, IReadOnlyList<AppInfo> fresh)
    {
        var before = Load(serial).ToDictionary(a => a.Package, StringComparer.Ordinal);
        var added = 0;
        var relabelled = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var app in fresh)
        {
            seen.Add(app.Package);
            if (!before.TryGetValue(app.Package, out var old))
            {
                added++;
            }
            else if (!string.Equals(old.Label, app.Label, StringComparison.Ordinal))
            {
                relabelled++;
            }
        }
        var removed = before.Keys.Count(p => !seen.Contains(p));

        Save(serial, fresh);
        foreach (var package in before.Keys.Where(p => !seen.Contains(p)))
        {
            DeleteIcon(serial, package);
        }

        var result = new AppReconcileResult(added, removed, relabelled, fresh.Count);
        _log?.Log(LogLevel.Info, $"Apps: {serial} — {result}");
        return result;
    }

    /// <summary>The cached icon bytes for one app, or null if it was never fetched or is unreadable.</summary>
    public byte[]? LoadIcon(string serial, string package)
    {
        try
        {
            var path = IconPathFor(serial, package);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Caches one app's PNG. Silently gives up on a write failure — see <see cref="Save"/>.</summary>
    public bool SaveIcon(string serial, string package, byte[] png)
    {
        try
        {
            var path = IconPathFor(serial, package);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, png);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Log(LogLevel.Warn, $"Apps: could not cache the icon for {package}: {ex.Message}");
            return false;
        }
    }

    private void DeleteIcon(string serial, string package)
    {
        try
        {
            var path = IconPathFor(serial, package);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An orphaned icon for an uninstalled app is harmless; it just wastes a few KB.
        }
    }

    /// <summary>
    /// Serials and package ids both become path segments, and neither is ours to trust — a package
    /// id is attacker-chosen on a rooted phone. Replace anything that isn't plainly safe so no
    /// value can climb out of the cache directory.
    /// </summary>
    internal static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "_";
        }
        var chars = value.Select(c =>
            char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray();
        var safe = new string(chars).Trim('.');
        return safe.Length == 0 ? "_" : safe;
    }

    /// <summary>On-disk shape. Deliberately its own type: the cache format is ours, not the wire's.</summary>
    private sealed class CachedApp
    {
        public string? Package { get; set; }
        public string? Label { get; set; }
        public string? VersionName { get; set; }
        public bool System { get; set; }
    }
}
