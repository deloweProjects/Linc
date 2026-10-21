using System.Globalization;
using System.Text.RegularExpressions;

namespace Linc.Desktop.Services.Tools;

/// <summary>What <c>dumpsys battery</c> says. Values the phone leaves out are null.</summary>
public sealed record BatteryInfo(int Level, bool Charging, bool Full, string? Source, double? TemperatureC, int? VoltageMv);

/// <summary>One line of <c>top</c>: a busy app and how much CPU it uses.</summary>
public sealed record BusyApp(int Pid, double Cpu, string Package);

/// <summary>Two numbers from the first line of <c>/proc/stat</c>, in jiffies since boot.</summary>
public sealed record CpuSample(long Idle, long Total);

/// <summary>
/// Turns the phone's own output into what Tools › Health and Tools › System show
/// (TOOLS.md §5–§6). Dependency-free, so tools/toolsim checks it against captured output. Every
/// parser returns null for output it doesn't understand: a value the phone doesn't share is
/// hidden, never an error.
/// </summary>
public static partial class HealthSystemParsers
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---- battery ---------------------------------------------------------------------------

    public static BatteryInfo? ParseBattery(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            // First one wins: some phones repeat keys in later blocks with stale values.
            values.TryAdd(key, line[(colon + 1)..].Trim());
        }
        if (!values.TryGetValue("level", out var levelText) || !int.TryParse(levelText, out var level)) return null;
        if (values.TryGetValue("scale", out var scaleText) && int.TryParse(scaleText, out var scale) && scale > 0 && scale != 100)
        {
            level = (int)Math.Round(level * 100.0 / scale);
        }
        var status = values.TryGetValue("status", out var s) && int.TryParse(s, out var st) ? st : 0;
        string? source = null;
        if (IsTrue(values, "AC powered")) source = "Charger";
        else if (IsTrue(values, "USB powered")) source = "USB";
        else if (IsTrue(values, "Wireless powered")) source = "Wireless";
        else if (IsTrue(values, "Dock powered")) source = "Dock";
        double? temp = values.TryGetValue("temperature", out var t) && int.TryParse(t, out var tenths) ? tenths / 10.0 : null;
        int? volt = values.TryGetValue("voltage", out var v) && int.TryParse(v, out var mv) && mv > 0 ? mv : null;
        // A few phones report the voltage in µV.
        if (volt > 100_000) volt /= 1000;
        // status 2 = charging, 5 = full (BatteryManager.BATTERY_STATUS_*).
        return new BatteryInfo(Math.Clamp(level, 0, 100), status == 2, status == 5, source, temp, volt);
    }

    private static bool IsTrue(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var v) && v.Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Charging power in watts from <c>current_now</c> and the battery voltage, or null when it
    /// can't be read or rounds to nothing. Phones report µA, a few mA; the sign differs by maker.
    /// </summary>
    public static double? ChargeWatts(string currentNow, int? voltageMv)
    {
        if (voltageMv is not > 0) return null;
        if (!long.TryParse(currentNow.Trim(), NumberStyles.AllowLeadingSign, Inv, out var current) || current == 0) return null;
        var microAmps = Math.Abs(current);
        if (microAmps < 20_000) microAmps *= 1000; // mA
        var watts = microAmps * (double)voltageMv / 1e9;
        return watts is >= 0.5 and < 300 ? watts : null;
    }

    public static string BatteryStatus(BatteryInfo b, double? watts)
    {
        var parts = new List<string>();
        if (b.Full) parts.Add("Full");
        else if (b.Charging) parts.Add("Charging");
        else parts.Add("On battery");
        if ((b.Charging || b.Full) && b.Source is not null) parts.Add(b.Source);
        if (b.Charging && watts is { } w) parts.Add(w.ToString(w < 10 ? "0.#" : "0", Inv) + " W");
        return string.Join(" · ", parts);
    }

    // ---- temperature -----------------------------------------------------------------------

    [GeneratedRegex(@"Temperature\{mValue=([-0-9.eENa]+), mType=(-?\d+), mName=([^,]*), mStatus=(-?\d+)\}")]
    private static partial Regex TemperatureLine();

    /// <summary>
    /// The phone's temperature from <c>dumpsys thermalservice</c>, read from the "Current
    /// temperatures from HAL" block only (an earlier block holds cached, older values). The skin
    /// sensor is what the phone feels like in the hand, so it wins when present; otherwise the
    /// hottest sensor.
    /// </summary>
    public static (double Celsius, string Name)? PhoneTemperature(string text)
    {
        var start = text.IndexOf("Current temperatures from HAL", StringComparison.Ordinal);
        if (start < 0) return null;
        var block = text[start..];
        var end = block.IndexOf("Current cooling devices", StringComparison.Ordinal);
        if (end > 0) block = block[..end];
        (double, string)? hottest = null, skin = null;
        foreach (Match m in TemperatureLine().Matches(block))
        {
            if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, Inv, out var c) || double.IsNaN(c) || c < -40 || c > 150) continue;
            var name = m.Groups[3].Value.Trim();
            var type = int.Parse(m.Groups[2].Value, Inv);
            if (type == 3 && (skin is null || c > skin.Value.Item1)) skin = (c, name);
            if (hottest is null || c > hottest.Value.Item1) hottest = (c, name);
        }
        return skin ?? hottest;
    }

    public static string TemperatureState(double celsius) => celsius < 38 ? "Cool" : celsius < 43 ? "Warm" : "Hot";

    // ---- storage ---------------------------------------------------------------------------

    /// <summary><c>df /data</c> (1K-blocks): total, used and available, in KB.</summary>
    public static (long TotalKb, long UsedKb, long AvailKb)? ParseDf(string text)
    {
        foreach (var line in Enumerable.Reverse(text.Split('\n')))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 6) continue;
            if (long.TryParse(f[1], out var total) && long.TryParse(f[2], out var used) && long.TryParse(f[3], out var avail) && total > 0)
            {
                return (total, used, avail);
            }
        }
        return null;
    }

    /// <summary>"61 GB", "5.1 GB", "820 MB" from a size in KB.</summary>
    public static string FormatKb(long kb)
    {
        var gb = kb / 1024.0 / 1024.0;
        if (gb >= 10) return Math.Round(gb).ToString(Inv) + " GB";
        if (gb >= 1) return gb.ToString("0.#", Inv) + " GB";
        return Math.Round(kb / 1024.0).ToString(Inv) + " MB";
    }

    // ---- CPU and memory --------------------------------------------------------------------

    public static CpuSample? ParseProcStat(string text)
    {
        var first = text.Split('\n').FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
        if (first is null) return null;
        var f = first.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(x => long.TryParse(x, out var n) ? n : -1).ToArray();
        if (f.Length < 4 || f.Any(n => n < 0)) return null;
        var idle = f[3] + (f.Length > 4 ? f[4] : 0); // idle + iowait
        var total = f.Take(8).Sum(); // user nice system idle iowait irq softirq steal
        return new CpuSample(idle, total);
    }

    /// <summary>CPU use between two samples, 0–100, or null if they don't make sense.</summary>
    public static double? CpuPercent(CpuSample before, CpuSample after)
    {
        var total = after.Total - before.Total;
        var idle = after.Idle - before.Idle;
        if (total <= 0 || idle < 0 || idle > total) return null;
        return Math.Round(100.0 * (total - idle) / total, 1);
    }

    public static (long TotalKb, long AvailableKb)? ParseMemInfo(string text)
    {
        long? total = null, avail = null;
        foreach (var line in text.Split('\n'))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 2 || !long.TryParse(f[1], out var kb)) continue;
            if (f[0] == "MemTotal:") total = kb;
            else if (f[0] == "MemAvailable:") avail = kb;
        }
        return total is > 0 && avail is >= 0 ? (total.Value, Math.Min(avail.Value, total.Value)) : null;
    }

    [GeneratedRegex(@"\x1b\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiEscape();

    /// <summary>
    /// The busiest apps from <c>top -b -n 1 -o PID,%CPU,RES,NAME</c>. Only app processes (a
    /// package name with a dot); a process like <c>com.x:service</c> counts as <c>com.x</c>, and
    /// each app appears once with its busiest process.
    /// </summary>
    public static IReadOnlyList<BusyApp> ParseTop(string text, int max = 5)
    {
        var apps = new List<BusyApp>();
        var inTable = false;
        foreach (var raw in AnsiEscape().Replace(text, "").Split('\n'))
        {
            var f = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 4) continue;
            if (f[0] == "PID") { inTable = true; continue; }
            if (!inTable || !int.TryParse(f[0], out var pid)) continue;
            if (!double.TryParse(f[1].TrimEnd('%'), NumberStyles.Float, Inv, out var cpu)) continue;
            var name = f[^1].TrimEnd('+');
            var colon = name.IndexOf(':');
            if (colon > 0) name = name[..colon];
            if (!IsPackageName(name)) continue;
            if (apps.Any(a => a.Package == name)) continue;
            apps.Add(new BusyApp(pid, cpu, name));
        }
        return apps.OrderByDescending(a => a.Cpu).Take(max).ToList();
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$")]
    private static partial Regex PackageName();

    public static bool IsPackageName(string name) => PackageName().IsMatch(name);

    // ---- phone details ---------------------------------------------------------------------

    /// <summary><c>wm size</c>: the size in use (an override wins over the physical size).</summary>
    public static string? ParseWmSize(string text)
    {
        string? physical = null, over = null;
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("Physical size:", StringComparison.Ordinal)) physical = t["Physical size:".Length..].Trim();
            else if (t.StartsWith("Override size:", StringComparison.Ordinal)) over = t["Override size:".Length..].Trim();
        }
        var size = over ?? physical;
        return size is not null && Regex.IsMatch(size, @"^\d+x\d+$") ? size.Replace("x", " × ") : null;
    }

    /// <summary><c>cat /proc/uptime</c> as "3 days 4 h", "5 h 12 min" or "12 min".</summary>
    public static string? FormatUptime(string text)
    {
        var first = text.Trim().Split(' ').FirstOrDefault();
        if (!double.TryParse(first, NumberStyles.Float, Inv, out var seconds) || seconds < 0) return null;
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays} day{((int)t.TotalDays == 1 ? "" : "s")} {t.Hours} h";
        if (t.TotalHours >= 1) return $"{t.Hours} h {t.Minutes} min";
        return $"{Math.Max(1, t.Minutes)} min";
    }

    /// <summary>A <c>getprop</c> value, or null when it's empty.</summary>
    public static string? Prop(string text)
    {
        var t = text.Trim();
        return t.Length == 0 || t.Contains('\n') ? null : t;
    }

    // ---- system settings -------------------------------------------------------------------

    /// <summary>A <c>settings get</c> value, or null for "null", empty or an error.</summary>
    public static string? Setting(string text)
    {
        var t = text.Trim();
        return t.Length == 0 || t == "null" || t.Contains(' ') ? null : t;
    }

    public static double? SettingNumber(string text) =>
        Setting(text) is { } v && double.TryParse(v, NumberStyles.Float, Inv, out var n) ? n : null;

    /// <summary>"1"/"0" (or any number) as on/off; null when the phone has no value.</summary>
    public static bool? SettingOn(string text) => SettingNumber(text) is { } n ? n != 0 : null;

    /// <summary><c>cmd uimode night</c>: "Night mode: yes" / "no" / "auto" / "custom".</summary>
    public static bool? ParseNightMode(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("Night mode:", StringComparison.OrdinalIgnoreCase)) return null;
        return t["Night mode:".Length..].Trim().Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><c>cmd wifi status</c>: its first line says "Wifi is enabled" or "Wifi is disabled".</summary>
    public static bool? ParseWifi(string text)
    {
        var first = text.TrimStart().Split('\n').FirstOrDefault()?.Trim() ?? "";
        if (first.Equals("Wifi is enabled", StringComparison.OrdinalIgnoreCase)) return true;
        if (first.Equals("Wifi is disabled", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    /// <summary>"true" / "false" (cmd location is-location-enabled).</summary>
    public static bool? ParseTrueFalse(string text) => text.Trim() switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };
}
