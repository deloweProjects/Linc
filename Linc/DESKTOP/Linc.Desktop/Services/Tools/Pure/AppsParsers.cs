using System.Globalization;
using System.Text.RegularExpressions;

namespace Linc.Desktop.Services.Tools;

/// <summary>One line of <c>pm list packages -f -U --show-versioncode</c>.</summary>
public sealed record PackageEntry(string Package, string ApkPath, long VersionCode, int Uid);

/// <summary>One runtime permission from <c>dumpsys package</c>.</summary>
public sealed record AppPermission(string Name, bool Granted)
{
    public string Label => AppsParsers.PermissionLabel(Name);
}

/// <summary>What <c>dumpsys package &lt;pkg&gt;</c> tells the Apps detail panel.</summary>
public sealed record PackageDetails(
    string VersionName,
    int TargetSdk,
    string FirstInstall,
    string LastUpdate,
    string Installer,
    IReadOnlyList<AppPermission> Permissions);

/// <summary>Sizes from <c>dumpsys diskstats</c>, in bytes.</summary>
public sealed record AppSizes(long App, long Data, long Cache)
{
    public long Total => App + Data + Cache;
}

/// <summary>
/// The Apps section's parsers (Vibe/claude-notes/linc-tools/TOOLS.md §3). Pure, so tools/toolsim
/// checks them against captured phone output.
/// </summary>
public static partial class AppsParsers
{
    /// <summary>The only package names Tools puts into a command, besides quoting them.</summary>
    public static bool IsValidPackage(string package) =>
        package.Length is > 0 and < 256 && PackagePattern().IsMatch(package);

    [GeneratedRegex(@"^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)*$")]
    private static partial Regex PackagePattern();

    // package:<apk path>=<package> versionCode:<n> uid:<n> — the path itself can hold '='
    // (Android 11+ puts base64 in /data/app/~~xyz==/), so the package is after the LAST '='
    // before " versionCode:".
    [GeneratedRegex(@"^package:(?<path>.+)=(?<pkg>[A-Za-z0-9_.]+)(?:\s+versionCode:(?<vc>-?\d+))?(?:\s+uid:(?<uid>\d+))?\s*$")]
    private static partial Regex PackageLine();

    public static IReadOnlyList<PackageEntry> ParsePackageList(string text)
    {
        var list = new List<PackageEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in Lines(text))
        {
            var line = raw.Trim();
            if (!line.StartsWith("package:", StringComparison.Ordinal)) continue;
            var m = PackageLine().Match(line);
            if (m.Success)
            {
                var pkg = m.Groups["pkg"].Value;
                if (!IsValidPackage(pkg) || !seen.Add(pkg)) continue;
                list.Add(new PackageEntry(pkg, m.Groups["path"].Value,
                    long.TryParse(m.Groups["vc"].Value, out var vc) ? vc : 0,
                    int.TryParse(m.Groups["uid"].Value, out var uid) ? uid : 0));
            }
            else
            {
                // Plain "package:com.foo" (no -f): still a package.
                var pkg = line["package:".Length..].Split(' ')[0];
                if (IsValidPackage(pkg) && seen.Add(pkg)) list.Add(new PackageEntry(pkg, "", 0, 0));
            }
        }
        return list;
    }

    /// <summary>Just the names, for the <c>-3</c> / <c>-d</c> sets.</summary>
    public static HashSet<string> PackageNames(string text) =>
        ParsePackageList(text).Select(p => p.Package).ToHashSet(StringComparer.Ordinal);

    /// <summary><c>dumpsys diskstats</c>: parallel arrays of names and sizes. Missing or odd
    /// output gives an empty map; sizes are then simply not shown.</summary>
    public static Dictionary<string, AppSizes> ParseDiskStats(string text)
    {
        string[]? names = null;
        long[]? app = null, data = null, cache = null;
        foreach (var raw in Lines(text))
        {
            var line = raw.Trim();
            if (line.StartsWith("Package Names:", StringComparison.Ordinal))
            {
                names = ArrayItems(line).Select(s => s.Trim('"')).ToArray();
            }
            else if (line.StartsWith("App Sizes:", StringComparison.Ordinal)) app = Numbers(line);
            else if (line.StartsWith("App Data Sizes:", StringComparison.Ordinal)) data = Numbers(line);
            else if (line.StartsWith("Cache Sizes:", StringComparison.Ordinal)) cache = Numbers(line);
        }
        var map = new Dictionary<string, AppSizes>(StringComparer.Ordinal);
        if (names is null || app is null || app.Length != names.Length) return map;
        for (var i = 0; i < names.Length; i++)
        {
            map[names[i]] = new AppSizes(app[i],
                data is { } d && d.Length == names.Length ? d[i] : 0,
                cache is { } c && c.Length == names.Length ? c[i] : 0);
        }
        return map;
    }

    private static IEnumerable<string> ArrayItems(string line)
    {
        var open = line.IndexOf('[');
        var close = line.LastIndexOf(']');
        if (open < 0 || close <= open) return [];
        var inner = line[(open + 1)..close];
        return inner.Length == 0 ? [] : inner.Split(',').Select(s => s.Trim());
    }

    private static long[]? Numbers(string line)
    {
        var items = ArrayItems(line).ToArray();
        var result = new long[items.Length];
        for (var i = 0; i < items.Length; i++)
        {
            if (!long.TryParse(items[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out result[i])) return null;
        }
        return result;
    }

    [GeneratedRegex(@"^\s*(?<name>[A-Za-z0-9_.]+): granted=(?<g>true|false)")]
    private static partial Regex PermissionLine();

    /// <summary>
    /// <c>dumpsys package &lt;pkg&gt;</c>. Only the first "Packages:" entry is read (the dump can
    /// also list a hidden system copy). Runtime permissions are the lines under "runtime
    /// permissions:" (under "User 0:" on Android 11+; directly under the package on older).
    /// </summary>
    public static PackageDetails ParseDetails(string text)
    {
        string version = "", first = "", last = "", installer = "";
        var target = 0;
        var perms = new List<AppPermission>();
        var seenPerms = new HashSet<string>(StringComparer.Ordinal);
        var packages = 0;
        var inRuntime = false;
        var runtimeIndent = -1;
        foreach (var raw in Lines(text))
        {
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("Package [", StringComparison.Ordinal))
            {
                packages++;
                if (packages > 1) break;
                continue;
            }
            if (packages == 0) continue;
            var indent = raw.Length - raw.TrimStart().Length;
            if (inRuntime)
            {
                var m = PermissionLine().Match(raw);
                if (m.Success && indent > runtimeIndent)
                {
                    var name = m.Groups["name"].Value;
                    if (seenPerms.Add(name)) perms.Add(new AppPermission(name, m.Groups["g"].Value == "true"));
                    continue;
                }
                inRuntime = false;
            }
            if (trimmed == "runtime permissions:")
            {
                inRuntime = true;
                runtimeIndent = indent;
                continue;
            }
            if (version.Length == 0 && trimmed.StartsWith("versionName=", StringComparison.Ordinal)) version = trimmed["versionName=".Length..];
            else if (first.Length == 0 && trimmed.StartsWith("firstInstallTime=", StringComparison.Ordinal)) first = trimmed["firstInstallTime=".Length..];
            else if (last.Length == 0 && trimmed.StartsWith("lastUpdateTime=", StringComparison.Ordinal)) last = trimmed["lastUpdateTime=".Length..];
            else if (installer.Length == 0 && trimmed.StartsWith("installerPackageName=", StringComparison.Ordinal)) installer = trimmed["installerPackageName=".Length..];
            if (target == 0)
            {
                var at = trimmed.IndexOf("targetSdk=", StringComparison.Ordinal);
                if (at >= 0)
                {
                    var digits = new string(trimmed[(at + "targetSdk=".Length)..].TakeWhile(char.IsDigit).ToArray());
                    _ = int.TryParse(digits, out target);
                }
            }
        }
        return new PackageDetails(version, target, first, last, installer == "null" ? "" : installer, perms);
    }

    /// <summary><c>pm path &lt;pkg&gt;</c>: one <c>package:/…/x.apk</c> line per split.</summary>
    public static IReadOnlyList<string> ParseApkPaths(string text) =>
        Lines(text).Select(l => l.Trim())
            .Where(l => l.StartsWith("package:/", StringComparison.Ordinal) && l.EndsWith(".apk", StringComparison.Ordinal))
            .Select(l => l["package:".Length..])
            .ToList();

    /// <summary>The home app's package from <c>cmd package resolve-activity --brief</c> (its last
    /// line is <c>pkg/activity</c>), or "" when there isn't one.</summary>
    public static string ParseLauncher(string text)
    {
        var last = Lines(text).Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
        var slash = last.IndexOf('/');
        var pkg = slash > 0 ? last[..slash] : "";
        return IsValidPackage(pkg) ? pkg : "";
    }

    private static readonly HashSet<string> Protected = new(StringComparer.Ordinal)
    {
        "com.android.systemui", "com.android.settings", "com.android.phone", "com.google.android.gms",
        "com.android.packageinstaller", "com.google.android.packageinstaller",
        "com.android.providers.telephony", "com.android.server.telecom", "android",
        "app.linc.android",
    };

    /// <summary>Why Tools won't remove, disable or clear this app, or null when it may.</summary>
    public static string? ProtectedReason(string package, string launcher)
    {
        if (package == "app.linc.android") return "This is Linc itself.";
        if (package.Length > 0 && package == launcher) return "This is the phone's home screen.";
        return Protected.Contains(package) ? "The phone needs this to work." : null;
    }

    // Names people know, for packages whose name says little.
    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.Ordinal)
    {
        ["com.spotify.music"] = "Spotify",
        ["com.whatsapp"] = "WhatsApp",
        ["com.whatsapp.w4b"] = "WhatsApp Business",
        ["com.instagram.android"] = "Instagram",
        ["com.android.chrome"] = "Chrome",
        ["com.google.android.youtube"] = "YouTube",
        ["com.google.android.gm"] = "Gmail",
        ["com.google.android.apps.maps"] = "Maps",
        ["com.google.android.apps.photos"] = "Photos",
        ["com.android.vending"] = "Play Store",
        ["com.google.android.gms"] = "Google Play services",
        ["com.facebook.katana"] = "Facebook",
        ["com.facebook.orca"] = "Messenger",
        ["com.zhiliaoapp.musically"] = "TikTok",
        ["com.twitter.android"] = "X",
        ["com.netflix.mediaclient"] = "Netflix",
        ["org.telegram.messenger"] = "Telegram",
        ["com.snapchat.android"] = "Snapchat",
        ["com.android.systemui"] = "System UI",
        ["com.android.settings"] = "Settings",
        ["com.android.phone"] = "Phone services",
        ["com.google.android.dialer"] = "Phone",
        ["com.google.android.apps.messaging"] = "Messages",
        ["com.google.android.apps.nexuslauncher"] = "Pixel Launcher",
        ["app.linc.android"] = "Linc",
    };

    private static readonly HashSet<string> Domains = new(StringComparer.Ordinal)
        { "com", "org", "net", "io", "app", "me", "co", "de", "tv", "in", "dev", "uk", "fr", "ru", "cn", "jp", "br" };

    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
        { "android", "google", "apps", "app", "mobile", "client", "sec", "samsung", "miui", "xiaomi", "huawei", "oneplus", "motorola", "oppo", "coloros", "lge", "htc", "sony" };

    /// <summary>
    /// A readable name from the package when the phone gives no label (no Linc app):
    /// com.spotify.music → "Spotify", com.example.notes → "Example Notes". Known apps by name;
    /// otherwise drop the domain and vendor words and capitalise what is left (at most two).
    /// </summary>
    public static string PrettyName(string package)
    {
        if (KnownNames.TryGetValue(package, out var known)) return known;
        var parts = package.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count > 1 && Domains.Contains(parts[0])) parts.RemoveAt(0);
        var useful = parts.Where(p => !Noise.Contains(p)).ToList();
        if (useful.Count == 0) useful = parts.Count > 0 ? [parts[^1]] : [package];
        if (useful.Count > 2) useful = useful.Skip(useful.Count - 2).ToList();
        return string.Join(' ', useful.Select(Capitalise));
    }

    private static string Capitalise(string word)
    {
        var w = word.Replace('_', ' ');
        return w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..];
    }

    private static readonly Dictionary<string, string> PermissionNames = new(StringComparer.Ordinal)
    {
        ["CAMERA"] = "Camera",
        ["RECORD_AUDIO"] = "Microphone",
        ["ACCESS_FINE_LOCATION"] = "Precise location",
        ["ACCESS_COARSE_LOCATION"] = "Approximate location",
        ["ACCESS_BACKGROUND_LOCATION"] = "Location all the time",
        ["POST_NOTIFICATIONS"] = "Notifications",
        ["READ_CONTACTS"] = "Read contacts",
        ["WRITE_CONTACTS"] = "Change contacts",
        ["GET_ACCOUNTS"] = "Accounts",
        ["READ_CALENDAR"] = "Read calendar",
        ["WRITE_CALENDAR"] = "Change calendar",
        ["READ_PHONE_STATE"] = "Phone state",
        ["CALL_PHONE"] = "Make calls",
        ["READ_CALL_LOG"] = "Read call log",
        ["WRITE_CALL_LOG"] = "Change call log",
        ["SEND_SMS"] = "Send SMS",
        ["RECEIVE_SMS"] = "Receive SMS",
        ["READ_SMS"] = "Read SMS",
        ["READ_EXTERNAL_STORAGE"] = "Read storage",
        ["WRITE_EXTERNAL_STORAGE"] = "Write storage",
        ["READ_MEDIA_IMAGES"] = "Photos",
        ["READ_MEDIA_VIDEO"] = "Videos",
        ["READ_MEDIA_AUDIO"] = "Music and audio",
        ["READ_MEDIA_VISUAL_USER_SELECTED"] = "Selected photos",
        ["BLUETOOTH_CONNECT"] = "Nearby devices (connect)",
        ["BLUETOOTH_SCAN"] = "Nearby devices (scan)",
        ["NEARBY_WIFI_DEVICES"] = "Nearby Wi-Fi devices",
        ["BODY_SENSORS"] = "Body sensors",
        ["ACTIVITY_RECOGNITION"] = "Physical activity",
    };

    /// <summary>android.permission.RECORD_AUDIO → "Microphone"; unknown ones read as words.</summary>
    public static string PermissionLabel(string permission)
    {
        var shortName = permission[(permission.LastIndexOf('.') + 1)..];
        if (permission.StartsWith("android.permission.", StringComparison.Ordinal) &&
            PermissionNames.TryGetValue(shortName, out var label)) return label;
        var words = shortName.Replace('_', ' ').ToLowerInvariant();
        return words.Length == 0 ? permission : char.ToUpperInvariant(words[0]) + words[1..];
    }

    /// <summary>1536 → "1.5 KB", 734003200 → "700 MB".</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        var format = value >= 100 || unit == 0 ? "0" : "0.#";
        return value.ToString(format, CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>A file-name-safe folder name ("Spotify 8.9.84").</summary>
    public static string SafeFolderName(string name)
    {
        var bad = new HashSet<char>("<>:\"/\\|?*");
        var clean = new string(name.Select(c => c < ' ' || bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "app" : clean;
    }

    private static IEnumerable<string> Lines(string text) => text.Replace("\r", "").Split('\n');
}
