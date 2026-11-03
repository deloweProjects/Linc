using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Linc.Desktop.Services.Tools;

// The Tools page's Advanced sections (TOOLS.md §7-10): Logs, Inspector, Macros and Console's Tab
// completion. Pure and dependency-free, so tools/toolsim checks them with captured phone output.

/// <summary>One logcat line in <c>-v threadtime</c> form.</summary>
public sealed record LogcatLine(string Time, int Pid, char Level, string Tag, string Message);

/// <summary>Tools › Logs (TOOLS.md §8): the logcat command and how its lines are read.</summary>
public static partial class LogcatParser
{
    /// <summary>The newest lines the view keeps (a ring: the oldest go first).</summary>
    public const int MaxLines = 20_000;

    // 10-27 12:28:30.123  1234  5678 I ActivityManager: Start proc …
    [GeneratedRegex(@"^\d\d-\d\d\s+(\d\d:\d\d:\d\d\.\d+)\s+(\d+)\s+\d+\s+([VDIWEFA])\s+(.*?)\s*:\s?(.*)$")]
    private static partial Regex Threadtime();

    /// <summary>A line, or null for logcat's own lines ("--------- beginning of main") and noise.</summary>
    public static LogcatLine? Parse(string line)
    {
        var m = Threadtime().Match(line.TrimEnd('\r'));
        if (!m.Success) return null;
        return new LogcatLine(m.Groups[1].Value, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            m.Groups[3].Value[0], m.Groups[4].Value, m.Groups[5].Value);
    }

    /// <summary>The level chips (All · Info · Warnings · Errors) as logcat's lowest priority shown.</summary>
    public static char MinLevel(string chip) => chip switch
    {
        "Info" => 'I',
        "Warnings" => 'W',
        "Errors" => 'E',
        _ => 'V',
    };

    /// <summary>The last 500 lines, then live; one app (by its process) and a lowest level when set.</summary>
    public static string Command(char minLevel, int? pid) =>
        "logcat -v threadtime -T 500" + (pid is { } p ? $" --pid={p}" : "") + (minLevel == 'V' ? "" : $" *:{minLevel}");

    /// <summary>The app's process id from <c>pidof</c> (the first, if it runs several), or null.</summary>
    public static int? ParsePid(string pidofOutput)
    {
        var first = pidofOutput.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0 ? pid : null;
    }

    /// <summary>The level as a word, for the screen reader and the saved file.</summary>
    public static string LevelName(char level) => level switch
    {
        'V' => "Verbose",
        'D' => "Debug",
        'I' => "Info",
        'W' => "Warning",
        'E' => "Error",
        'F' or 'A' => "Fatal",
        _ => "",
    };

    /// <summary>A line as it goes into a saved file.</summary>
    public static string Format(LogcatLine l) => $"{l.Time} {l.Pid,5} {l.Level} {l.Tag}: {l.Message}";
}

/// <summary>One element of the phone's screen, from <c>uiautomator dump</c>.</summary>
public sealed record UiNode(int Left, int Top, int Right, int Bottom, string Text, string ResourceId,
    string Description, string ClassName, string Package, bool Clickable)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public int CenterX => (Left + Right) / 2;
    public int CenterY => (Top + Bottom) / 2;

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    /// <summary>The element's own words, or what kind of thing it is.</summary>
    public string Label =>
        Text.Length > 0 ? Text
        : Description.Length > 0 ? Description
        : ResourceId.Length > 0 ? ResourceId[(ResourceId.LastIndexOf('/') + 1)..]
        : ClassName[(ClassName.LastIndexOf('.') + 1)..];

    /// <summary>How a macro finds this element again: its text, else its id, else its description.</summary>
    public string Selector =>
        Text.Length > 0 ? "text=" + Text
        : ResourceId.Length > 0 ? "id=" + ResourceId
        : Description.Length > 0 ? "desc=" + Description
        : $"point={CenterX},{CenterY}";
}

/// <summary>Tools › Inspector (TOOLS.md §7): the dump command and how it is read.</summary>
public static partial class UiDump
{
    /// <summary>Dump to a file, print it, remove it. uiautomator's own errors come through as text.</summary>
    public const string Command = "uiautomator dump /sdcard/linc-ui.xml; cat /sdcard/linc-ui.xml 2>/dev/null; rm -f /sdcard/linc-ui.xml";

    [GeneratedRegex(@"^\[(-?\d+),(-?\d+)\]\[(-?\d+),(-?\d+)\]$")]
    private static partial Regex Bounds();

    /// <summary>Every element with a size, biggest first (so small ones draw on top). Empty when
    /// the output holds no dump; <see cref="Problem"/> then says why.</summary>
    public static IReadOnlyList<UiNode> Parse(string output)
    {
        var start = output.IndexOf("<?xml", StringComparison.Ordinal);
        if (start < 0) start = output.IndexOf("<hierarchy", StringComparison.Ordinal);
        var end = output.LastIndexOf("</hierarchy>", StringComparison.Ordinal);
        if (start < 0 || end < start) return [];
        XDocument doc;
        try
        {
            doc = XDocument.Parse(output[start..(end + "</hierarchy>".Length)]);
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }
        var nodes = new List<UiNode>();
        foreach (var n in doc.Descendants("node"))
        {
            var m = Bounds().Match((string?)n.Attribute("bounds") ?? "");
            if (!m.Success) continue;
            int Num(int g) => int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
            var node = new UiNode(Num(1), Num(2), Num(3), Num(4),
                (string?)n.Attribute("text") ?? "", (string?)n.Attribute("resource-id") ?? "",
                (string?)n.Attribute("content-desc") ?? "", (string?)n.Attribute("class") ?? "",
                (string?)n.Attribute("package") ?? "", (string?)n.Attribute("clickable") == "true");
            if (node.Width > 0 && node.Height > 0) nodes.Add(node);
        }
        return nodes.OrderByDescending(n => (long)n.Width * n.Height).ToList();
    }

    /// <summary>Why there is no dump, in words; null when the output looks fine.</summary>
    public static string? Problem(string output)
    {
        if (output.Contains("</hierarchy>", StringComparison.Ordinal)) return null;
        if (output.Contains("idle state", StringComparison.OrdinalIgnoreCase))
            return "The screen is busy (an animation or a video). Refresh in a moment, or turn animations off in System.";
        if (output.Contains("null root node", StringComparison.OrdinalIgnoreCase))
            return "The screen has nothing to read yet. Refresh in a moment.";
        return "Couldn't read the screen's elements. Refresh to try again.";
    }

    /// <summary>The element under a point: the smallest that holds it, a clickable one first.</summary>
    public static UiNode? At(IReadOnlyList<UiNode> nodes, int x, int y, bool clickableOnly = false)
    {
        UiNode? best = null, bestClickable = null;
        foreach (var n in nodes)
        {
            if (!n.Contains(x, y)) continue;
            if (best is null || Area(n) <= Area(best)) best = n;
            if (n.Clickable && (bestClickable is null || Area(n) <= Area(bestClickable))) bestClickable = n;
        }
        return clickableOnly ? bestClickable : bestClickable ?? best;
    }

    /// <summary>The element a <see cref="UiNode.Selector"/> names, or null when it isn't on screen.</summary>
    public static UiNode? Find(IReadOnlyList<UiNode> nodes, string selector)
    {
        var eq = selector.IndexOf('=');
        if (eq < 0) return null;
        var (kind, value) = (selector[..eq], selector[(eq + 1)..]);
        return kind switch
        {
            "text" => Smallest(nodes.Where(n => n.Text == value)),
            "id" => Smallest(nodes.Where(n => n.ResourceId == value)),
            "desc" => Smallest(nodes.Where(n => n.Description == value)),
            _ => null,
        };
    }

    /// <summary>True when some element shows <paramref name="text"/> (macros' "Wait until text").</summary>
    public static bool Shows(IReadOnlyList<UiNode> nodes, string text) =>
        nodes.Any(n => n.Text.Contains(text, StringComparison.OrdinalIgnoreCase) || n.Description.Contains(text, StringComparison.OrdinalIgnoreCase));

    private static UiNode? Smallest(IEnumerable<UiNode> nodes) => nodes.OrderBy(Area).FirstOrDefault();

    private static long Area(UiNode n) => (long)n.Width * n.Height;
}

/// <summary>What one macro step does (TOOLS.md §9).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MacroStepKind>))]
public enum MacroStepKind
{
    Key, Text, Pin, Tap, TapElement, Swipe, OpenApp, OpenLink, OpenSettings, Wait, WaitApp, WaitText, WaitScreenOn, Shell,
}

/// <summary>One step. <see cref="Value"/> is the key, text, element selector, package, link,
/// settings screen or shell command; the numbers are points and seconds.</summary>
public sealed record MacroStep(MacroStepKind Kind, string Value = "", int X = 0, int Y = 0, int X2 = 0, int Y2 = 0, double Seconds = 0);

/// <summary>A saved macro: a name, a Segoe glyph and its steps.</summary>
public sealed record Macro(string Id, string Name, string Glyph, IReadOnlyList<MacroStep> Steps);

/// <summary>Tools › Macros: what each step sends, what it says, the templates and the file.</summary>
public static partial class MacroSteps
{
    /// <summary>How long a "Wait until" step waits by default.</summary>
    public const double DefaultWaitSeconds = 10;

    [GeneratedRegex(@"^[A-Za-z0-9_.]+$")]
    private static partial Regex Word();

    /// <summary>The key names a step takes (KEYCODE_ is optional), and plain numbers.</summary>
    public static string? KeyCode(string key)
    {
        var k = key.Trim().ToUpperInvariant();
        if (k.Length == 0) return null;
        if (int.TryParse(k, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return n is > 0 and < 400 ? n.ToString(CultureInfo.InvariantCulture) : null;
        if (!k.StartsWith("KEYCODE_", StringComparison.Ordinal)) k = "KEYCODE_" + k;
        return Word().IsMatch(k) ? k : null;
    }

    /// <summary>The shell command for a step that is one command; null for waits, the PIN (asked
    /// each run), "Tap element" (it needs the screen first) and a step that isn't filled in.</summary>
    public static string? Command(MacroStep s) => s.Kind switch
    {
        MacroStepKind.Key => KeyCode(s.Value) is { } code ? "input keyevent " + code : null,
        MacroStepKind.Text => TypeCommands.LineCommand(s.Value, enter: false, out _),
        MacroStepKind.Tap => $"input tap {s.X} {s.Y}",
        MacroStepKind.Swipe => $"input swipe {s.X} {s.Y} {s.X2} {s.Y2} {SwipeMs(s)}",
        MacroStepKind.OpenApp => Word().IsMatch(s.Value) ? $"monkey -p {s.Value} -c android.intent.category.LAUNCHER 1" : null,
        MacroStepKind.OpenLink => Uri.TryCreate(s.Value.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
            ? "am start -a android.intent.action.VIEW -d " + AdbQuote.Arg(u.ToString()) : null,
        MacroStepKind.OpenSettings => Word().IsMatch(s.Value) ? "am start -a " + s.Value : null,
        MacroStepKind.Shell => s.Value.Trim().Length > 0 ? s.Value.Trim() : null,
        _ => null,
    };

    /// <summary>The step in words, for the list and the progress line.</summary>
    public static string Describe(MacroStep s) => s.Kind switch
    {
        MacroStepKind.Key => $"Press {(KeyCode(s.Value) ?? s.Value).Replace("KEYCODE_", "").Replace('_', ' ').ToLowerInvariant()}",
        MacroStepKind.Text => $"Type \"{s.Value}\"",
        MacroStepKind.Pin => "Type the PIN (asked each time, never saved)",
        MacroStepKind.Tap => $"Tap at {s.X}, {s.Y}",
        MacroStepKind.TapElement => $"Tap \"{s.Value[(s.Value.IndexOf('=') + 1)..]}\"",
        MacroStepKind.Swipe => $"Swipe {s.X},{s.Y} → {s.X2},{s.Y2}",
        MacroStepKind.OpenApp => $"Open {s.Value}",
        MacroStepKind.OpenLink => $"Open {s.Value}",
        MacroStepKind.OpenSettings => $"Open settings: {s.Value.Replace("android.settings.", "").Replace('_', ' ').ToLowerInvariant()}",
        MacroStepKind.Wait => $"Wait {Seconds(s.Seconds)}",
        MacroStepKind.WaitApp => $"Wait until {s.Value} is on screen",
        MacroStepKind.WaitText => $"Wait until \"{s.Value}\" shows",
        MacroStepKind.WaitScreenOn => "Wait until the screen is on",
        MacroStepKind.Shell => $"Run: {s.Value}",
        _ => s.Kind.ToString(),
    };

    /// <summary>Steps that change things a plain tap can't, asked before each run (PLAN.md §2 rule 4).</summary>
    public static bool IsRisky(MacroStep s) => s.Kind == MacroStepKind.Shell && ConsoleRisk.Describe(s.Value) is not null;

    /// <summary>"Wait until" checks: the command, and whether its output means done.</summary>
    public const string ForegroundCommand = "dumpsys activity activities | grep -E 'topResumedActivity|mResumedActivity'";
    public const string ScreenCommand = "dumpsys power | grep mWakefulness=";

    public static bool AppOnScreen(string foregroundOutput, string package) =>
        package.Length > 0 && foregroundOutput.Contains(package + "/", StringComparison.Ordinal);

    public static bool ScreenOn(string powerOutput) => powerOutput.Contains("Awake", StringComparison.Ordinal);

    private static int SwipeMs(MacroStep s) => s.Seconds > 0 ? (int)Math.Clamp(s.Seconds * 1000, 50, 5000) : 300;

    private static string Seconds(double s) => s == 1 ? "1 second" : $"{s.ToString("0.#", CultureInfo.InvariantCulture)} seconds";

    /// <summary>Ready-made macros for the "+ New" menu (TOOLS.md §9).</summary>
    public static IReadOnlyList<Macro> Templates { get; } =
    [
        new("wake-unlock", "Wake & unlock", "\uE72E",
        [
            new(MacroStepKind.Key, "WAKEUP"),
            new(MacroStepKind.Wait, Seconds: 0.5),
            new(MacroStepKind.Key, "MENU"),
            new(MacroStepKind.Wait, Seconds: 0.5),
            new(MacroStepKind.Pin),
            new(MacroStepKind.Key, "ENTER"),
        ]),
        new("clean-screenshot", "Clean screenshot", "\uE722",
        [
            .. CaptureCommands.DemoEnter.Select(c => new MacroStep(MacroStepKind.Shell, c)),
            new(MacroStepKind.Wait, Seconds: 1),
            new(MacroStepKind.Shell, "screencap -p /sdcard/Pictures/linc-clean.png"),
            new(MacroStepKind.Shell, CaptureCommands.DemoExit),
        ]),
        new("night", "Night mode", "\uE708",
        [
            new(MacroStepKind.Shell, "cmd uimode night yes"),
            new(MacroStepKind.Shell, "settings put system screen_brightness_mode 0 && settings put system screen_brightness 50"),
        ]),
    ];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Serialize(IReadOnlyList<Macro> macros) => JsonSerializer.Serialize(macros, Json);

    /// <summary>The saved macros; an unreadable file gives none rather than throwing.</summary>
    public static IReadOnlyList<Macro> Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<Macro>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>
/// Tools › Console's Tab (TOOLS.md §10): completes the word at the end of the line from what the
/// phone has (commands, packages, settings keys), loaded once per phone.
/// </summary>
public static class ConsoleComplete
{
    public sealed record Sources(IReadOnlyList<string> Commands, IReadOnlyList<string> Packages, IReadOnlyList<string> SettingsKeys);

    /// <summary>What the console asks the phone for, once: binaries, packages, then settings keys.</summary>
    public const string CommandsQuery = "ls /system/bin /system/xbin 2>/dev/null";
    public const string PackagesQuery = "pm list packages";
    public const string SettingsQuery = "settings list system; settings list secure; settings list global";

    public static Sources Read(string commands, string packages, string settings) => new(
        Lines(commands).Where(l => !l.EndsWith(':') && !l.Contains('/')).Distinct().Order(StringComparer.Ordinal).ToList(),
        Lines(packages).Select(l => l.StartsWith("package:", StringComparison.Ordinal) ? l["package:".Length..] : l).Distinct().Order(StringComparer.Ordinal).ToList(),
        Lines(settings).Select(l => l.Split('=')[0]).Where(k => k.Length > 0).Distinct().Order(StringComparer.Ordinal).ToList());

    /// <summary>
    /// The line with its last word completed as far as all matches agree, and the matches. The
    /// first word completes from commands; after <c>settings get|put|delete &lt;table&gt;</c> from
    /// settings keys; anything else from packages.
    /// </summary>
    public static (string Line, IReadOnlyList<string> Matches) Complete(string line, Sources sources)
    {
        var cut = line.LastIndexOf(' ') + 1;
        var (head, word) = (line[..cut], line[cut..]);
        var before = head.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        IReadOnlyList<string> pool =
            before.Length == 0 ? sources.Commands
            : before.Length >= 3 && before[^3] == "settings" && before[^2] is "get" or "put" or "delete" ? sources.SettingsKeys
            : before.Length == 1 && before[0] == "settings" ? ["get", "put", "delete", "list"]
            : before.Length == 2 && before[0] == "settings" ? ["system", "secure", "global"]
            : sources.Packages;
        var matches = pool.Where(p => p.StartsWith(word, StringComparison.Ordinal)).Take(200).ToList();
        if (matches.Count == 0) return (line, matches);
        if (matches.Count == 1) return (head + matches[0] + " ", matches);
        var common = matches[0];
        foreach (var m in matches)
        {
            var i = 0;
            while (i < common.Length && i < m.Length && common[i] == m[i]) i++;
            common = common[..i];
        }
        return (head + (common.Length > word.Length ? common : word), matches);
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
}
