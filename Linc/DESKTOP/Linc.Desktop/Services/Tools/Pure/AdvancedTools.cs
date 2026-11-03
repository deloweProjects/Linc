using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
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

/// <summary>What one macro step does (TOOLS.md §9). From <see cref="SendClipboard"/> on, the steps
/// go over Linc's own link or run on this PC (the owner, 2026-11-03: "add a macros section in
/// Tools on PC and phone").</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MacroStepKind>))]
public enum MacroStepKind
{
    Key, Text, Pin, Tap, TapElement, Swipe, OpenApp, OpenLink, OpenSettings, Wait, WaitApp, WaitText, WaitScreenOn, Shell,
    SendClipboard, Ring, Dnd, Screenshot, Mirror, Media, PcOpen,
}

/// <summary>What a step needs before it can run: the phone over ADB, Linc's link, or nothing.</summary>
public enum MacroNeed { Adb, Link, Nothing }

/// <summary>One entry of the "Add step" menu: what it's called, what its box asks for.</summary>
public sealed record MacroStepInfo(MacroStepKind Kind, string Title, string Glyph, MacroNeed Need, string ValueLabel = "", string Example = "")
{
    public bool HasValue => ValueLabel.Length > 0;

    public override string ToString() => Title;
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

    /// <summary>The longest a Wait step may hold a run (ten minutes).</summary>
    public const double MaxWaitSeconds = 600;

    /// <summary>The phone's media actions (<c>media.control</c>).</summary>
    public static IReadOnlyList<string> MediaActions { get; } = ["play", "pause", "next", "prev"];

    /// <summary>The "Add step" menu, in its order. A catalogue command comes in as a Shell step
    /// from the command picker; Tap element from the Inspector.</summary>
    public static IReadOnlyList<MacroStepInfo> Kinds { get; } =
    [
        new(MacroStepKind.OpenApp, "Open an app on the phone", "\uE71D", MacroNeed.Adb, "Package", "com.spotify.music"),
        new(MacroStepKind.OpenLink, "Open a link on the phone", "\uE774", MacroNeed.Adb, "Link", "https://example.com"),
        new(MacroStepKind.OpenSettings, "Open a settings screen", "\uE713", MacroNeed.Adb, "Settings action", "android.settings.WIFI_SETTINGS"),
        new(MacroStepKind.Key, "Press a key", "\uE765", MacroNeed.Adb, "Key", "HOME"),
        new(MacroStepKind.Text, "Type text", "\uE8D2", MacroNeed.Adb, "Text", "hello"),
        new(MacroStepKind.Pin, "Type the PIN (asked each run)", "\uE72E", MacroNeed.Adb),
        new(MacroStepKind.Tap, "Tap a point", "\uE7C9", MacroNeed.Adb, "x, y", "540, 1200"),
        new(MacroStepKind.Swipe, "Swipe", "\uE7C9", MacroNeed.Adb, "x, y, x2, y2", "540, 1800, 540, 600"),
        new(MacroStepKind.Shell, "Shell command", "\uE756", MacroNeed.Adb, "Command", "cmd uimode night yes"),
        new(MacroStepKind.Screenshot, "Take a screenshot", "\uE722", MacroNeed.Adb),
        new(MacroStepKind.SendClipboard, "Send to the phone's clipboard", "\uE77F", MacroNeed.Link, "Text (empty: this PC's clipboard)"),
        new(MacroStepKind.Ring, "Ring the phone", "\uEA8F", MacroNeed.Link),
        new(MacroStepKind.Dnd, "Do Not Disturb", "\uE708", MacroNeed.Link, "on or off", "on"),
        new(MacroStepKind.Media, "Phone media", "\uE768", MacroNeed.Link, "play, pause, next or prev", "pause"),
        new(MacroStepKind.Mirror, "Mirror the phone", "\uE8AF", MacroNeed.Link),
        new(MacroStepKind.PcOpen, "Open a file or app on this PC", "\uE8E5", MacroNeed.Nothing, "File, app or link", @"C:\Windows\notepad.exe"),
        new(MacroStepKind.Wait, "Wait", "\uE916", MacroNeed.Nothing, "Seconds", "2"),
        new(MacroStepKind.WaitApp, "Wait until an app is on screen", "\uE916", MacroNeed.Adb, "Package", "com.spotify.music"),
        new(MacroStepKind.WaitText, "Wait until text shows", "\uE916", MacroNeed.Adb, "Text", "Done"),
        new(MacroStepKind.WaitScreenOn, "Wait until the screen is on", "\uE916", MacroNeed.Adb),
    ];

    public static MacroNeed Need(MacroStepKind kind) =>
        Kinds.FirstOrDefault(k => k.Kind == kind)?.Need ?? MacroNeed.Adb;

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
        MacroStepKind.SendClipboard => s.Value.Length == 0 ? "Send this PC's clipboard to the phone" : $"Send \"{s.Value}\" to the phone's clipboard",
        MacroStepKind.Ring => "Ring the phone",
        MacroStepKind.Dnd => $"Do Not Disturb {s.Value}",
        MacroStepKind.Screenshot => "Take a screenshot",
        MacroStepKind.Mirror => "Mirror the phone",
        MacroStepKind.Media => $"Phone media: {s.Value}",
        MacroStepKind.PcOpen => $"Open {(Path.GetFileName(s.Value.TrimEnd('\\')) is { Length: > 0 } name ? name : s.Value)} on this PC",
        _ => s.Kind.ToString(),
    };

    /// <summary>Why a step can't run as it is filled in, or null when it can (the editor's check).</summary>
    public static string? Problem(MacroStep s) => s.Kind switch
    {
        MacroStepKind.Key => KeyCode(s.Value) is null ? "Give a key like HOME, BACK or a number" : null,
        MacroStepKind.Text => Command(s) is null ? "Fill in the text" : null,
        MacroStepKind.Shell => CommandCatalog.Placeholder().Match(s.Value) is { Success: true } m ? $"Fill in {m.Value}"
            : Command(s) is null ? "Fill in the command" : null,
        MacroStepKind.OpenApp or MacroStepKind.WaitApp => Word().IsMatch(s.Value) && s.Value.Contains('.') ? null : "Give the app's package, like com.spotify.music",
        MacroStepKind.OpenLink => Command(s) is null ? "Give a whole link, starting with https://" : null,
        MacroStepKind.OpenSettings => Command(s) is null ? "Give a settings action, like android.settings.WIFI_SETTINGS" : null,
        MacroStepKind.Tap => s.X < 0 || s.Y < 0 ? "Give the point as x, y" : null,
        MacroStepKind.Swipe => s.X < 0 || s.Y < 0 || s.X2 < 0 || s.Y2 < 0 ? "Give the swipe as x, y, x2, y2" : null,
        MacroStepKind.WaitText => s.Value.Trim().Length == 0 ? "Give the text to wait for" : null,
        MacroStepKind.Dnd => s.Value is "on" or "off" ? null : "Say on or off",
        MacroStepKind.Media => MediaActions.Contains(s.Value) ? null : "Say play, pause, next or prev",
        MacroStepKind.PcOpen => s.Value.Trim().Length == 0 ? "Give a file, an app or a link" : null,
        MacroStepKind.Wait => s.Seconds is >= 0.1 and <= MaxWaitSeconds ? null : "Give seconds, from 0.1 to 600",
        _ => null,
    };

    /// <summary>A step from the "Add step" box: <paramref name="text"/> is what was typed. Points
    /// are "x, y" (and "x2, y2" for a swipe); a Wait's seconds are its number.</summary>
    public static MacroStep FromInput(MacroStepKind kind, string text)
    {
        var t = text.Trim();
        switch (kind)
        {
            case MacroStepKind.Wait:
                return new(kind, Seconds: double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec) ? sec : 0);
            case MacroStepKind.WaitApp or MacroStepKind.WaitText or MacroStepKind.WaitScreenOn:
                return new(kind, t, Seconds: DefaultWaitSeconds);
            case MacroStepKind.Tap or MacroStepKind.Swipe:
                var n = t.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : -1).ToArray();
                int At(int i) => i < n.Length ? n[i] : -1;
                return new(kind, X: At(0), Y: At(1), X2: At(2), Y2: At(3));
            case MacroStepKind.Dnd or MacroStepKind.Media:
                return new(kind, t.ToLowerInvariant());
            default:
                return new(kind, t);
        }
    }

    /// <summary>"Step 2 (Ring the phone) failed: …", the line a failed run leaves.</summary>
    public static string FailureText(int index, MacroStep step, string reason) =>
        $"Step {index + 1} ({Describe(step)}) failed: {reason}";

    /// <summary>A list with one item moved (the ↑ ↓ buttons); out of range, no change.</summary>
    public static IReadOnlyList<T> Moved<T>(IReadOnlyList<T> list, int from, int to)
    {
        if (from < 0 || from >= list.Count || to < 0 || to >= list.Count || from == to) return list;
        var copy = list.ToList();
        var item = copy[from];
        copy.RemoveAt(from);
        copy.Insert(to, item);
        return copy;
    }

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

    /// <summary>Ready-made macros for the "+ New" menu (TOOLS.md §9). A first look gets them all,
    /// the owner's "Presenting" first.</summary>
    public static IReadOnlyList<Macro> Templates { get; } =
    [
        new("presenting", "Presenting", "",
        [
            new(MacroStepKind.Dnd, "on"),
            new(MacroStepKind.Media, "pause"),
            new(MacroStepKind.Mirror),
        ]),
        new("done-presenting", "Done presenting", "", [new(MacroStepKind.Dnd, "off")]),
        new("find-phone", "Find my phone", "", [new(MacroStepKind.Ring)]),
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
    public static IReadOnlyList<Macro> Deserialize(string json) => TryDeserialize(json) ?? [];

    /// <summary>The saved macros, or null when the file isn't one (so the caller keeps it rather
    /// than writing over it). A step this Linc doesn't know, from a newer one, is left out; the
    /// rest of its macro stays.</summary>
    public static IReadOnlyList<Macro>? TryDeserialize(string json)
    {
        JsonArray? list;
        try
        {
            list = JsonNode.Parse(json) as JsonArray;
        }
        catch (JsonException)
        {
            return null;
        }
        if (list is null) return null;
        var macros = new List<Macro>();
        foreach (var node in list)
        {
            if (node is not JsonObject m) continue;
            string Str(string key) => m[key] is JsonValue v && v.TryGetValue<string>(out var t) ? t : "";
            if (Str("Id").Length == 0) continue;
            var steps = new List<MacroStep>();
            foreach (var s in m["Steps"] as JsonArray ?? [])
            {
                try
                {
                    if (s?.Deserialize<MacroStep>(Json) is { } step) steps.Add(step);
                }
                catch (JsonException)
                {
                    // A step from a newer Linc: left out, the rest of the macro stays.
                }
            }
            macros.Add(new Macro(Str("Id"), Str("Name") is { Length: > 0 } name ? name : "Macro",
                Str("Glyph") is { Length: > 0 } glyph ? glyph : "", steps));
        }
        return macros;
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
