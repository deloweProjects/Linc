using System.IO;
using System.IO.Compression;
using System.Text;

namespace Linc.Desktop.Services.Tools;

/// <summary>Capture's commands (TOOLS.md §4), in one place so tools/toolsim checks them.</summary>
public static class CaptureCommands
{
    public const string RecordingPath = "/sdcard/linc-rec.mp4";

    /// <summary>The longest one recording runs; screenrecord's own limit is 180 s.</summary>
    public const int RecordLimitSeconds = 180;

    /// <summary>0 = 720p, 1 = 1080p, 2 = the phone's own size.</summary>
    public static string Screenrecord(int quality) => quality switch
    {
        1 => $"screenrecord --size 1920x1080 --bit-rate 16000000 --time-limit {RecordLimitSeconds} {RecordingPath}",
        2 => $"screenrecord --bit-rate 20000000 --time-limit {RecordLimitSeconds} {RecordingPath}",
        _ => $"screenrecord --size 1280x720 --bit-rate 8000000 --time-limit {RecordLimitSeconds} {RecordingPath}",
    };

    /// <summary>screenrecord finishes its file on SIGINT, like Ctrl+C.</summary>
    public const string StopRecording = "pkill -INT screenrecord";

    public const string StopRecordingFallback = "kill -2 $(pidof screenrecord)";

    private const string Demo = "am broadcast -a com.android.systemui.demo -e command ";

    /// <summary>Clean status bar: 12:00, full battery, full Wi-Fi, no notification icons.</summary>
    public static IReadOnlyList<string> DemoEnter { get; } =
    [
        "settings put global sysui_demo_allowed 1",
        Demo + "enter",
        Demo + "clock -e hhmm 1200",
        Demo + "battery -e level 100 -e plugged false",
        Demo + "network -e wifi show -e level 4",
        Demo + "notifications -e visible false",
    ];

    public const string DemoExit = Demo + "exit";

    /// <summary>"phone-20261021-140512" style names, so captures sort by time.</summary>
    public static string FileName(DateTime when, string extension) => $"phone-{when:yyyyMMdd-HHmmss}.{extension}";

    /// <summary>mm:ss for the record button.</summary>
    public static string Clock(int seconds) => $"{seconds / 60:00}:{seconds % 60:00}";
}

/// <summary>
/// Tells whether a screenshot came back (nearly) all black: what Android hands back for an app
/// that blocks screenshots (FLAG_SECURE, banking apps). A small PNG reader (8-bit RGB/RGBA, the
/// only kinds screencap writes); anything else is "not black", so it never cries wolf.
/// </summary>
public static class ScreenshotCheck
{
    public static bool LooksBlack(byte[] png)
    {
        try
        {
            return LooksBlackCore(png);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    private static bool LooksBlackCore(byte[] png)
    {
        if (png.Length < 33 || png[0] != 0x89 || png[1] != (byte)'P' || png[2] != (byte)'N' || png[3] != (byte)'G') return false;
        int width = 0, height = 0, channels = 0;
        using var idat = new MemoryStream();
        var pos = 8;
        while (pos + 8 <= png.Length)
        {
            var length = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
            var type = Encoding.ASCII.GetString(png, pos + 4, 4);
            var data = pos + 8;
            if (length < 0 || data + length > png.Length) return false;
            if (type == "IHDR")
            {
                width = (png[data] << 24) | (png[data + 1] << 16) | (png[data + 2] << 8) | png[data + 3];
                height = (png[data + 4] << 24) | (png[data + 5] << 16) | (png[data + 6] << 8) | png[data + 7];
                var depth = png[data + 8];
                var color = png[data + 9];
                var interlace = png[data + 12];
                channels = color switch { 2 => 3, 6 => 4, _ => 0 };
                if (depth != 8 || channels == 0 || interlace != 0) return false;
            }
            else if (type == "IDAT")
            {
                idat.Write(png, data, length);
            }
            else if (type == "IEND")
            {
                break;
            }
            pos = data + length + 4; // skip the CRC
        }
        if (width <= 0 || height <= 0 || channels == 0) return false;

        var stride = width * channels;
        var size = (long)(stride + 1) * height;
        if (size > 200_000_000) return false; // bigger than any phone screen
        var raw = new byte[size];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            var read = 0;
            while (read < raw.Length)
            {
                var n = z.Read(raw, read, raw.Length - read);
                if (n == 0) return false;
                read += n;
            }
        }

        var prev = new byte[stride];
        var line = new byte[stride];
        long bright = 0, total = (long)width * height;
        for (var y = 0; y < height; y++)
        {
            var offset = y * (stride + 1);
            var filter = raw[offset];
            for (var x = 0; x < stride; x++)
            {
                int a = x >= channels ? line[x - channels] : 0;
                int b = prev[x];
                int c = x >= channels ? prev[x - channels] : 0;
                int v = raw[offset + 1 + x];
                v += filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException("bad PNG filter"),
                };
                line[x] = (byte)v;
            }
            for (var x = 0; x < stride; x += channels)
            {
                if (line[x] > 24 || line[x + 1] > 24 || line[x + 2] > 24) bright++;
            }
            (prev, line) = (line, prev);
        }
        // Nearly every pixel dark: a blocked screen (a dark app still has text and icons).
        return bright * 1000 < total;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}

/// <summary>Console's verbs that change or wipe things (TOOLS.md §10): the first per session asks.</summary>
public static class ConsoleRisk
{
    private static readonly (string Verb, string Why)[] Verbs =
    [
        ("pm clear", "wipes an app's data"),
        ("pm uninstall", "removes an app"),
        ("cmd package uninstall", "removes an app"),
        ("settings put", "changes a phone setting"),
        ("rm", "deletes files"),
        ("uninstall", "removes an app"),
        ("reboot", "restarts the phone"),
        ("wm", "changes the screen size or density"),
        ("svc", "switches Wi-Fi, data or power"),
        ("dd", "writes raw data"),
        ("mkfs", "formats storage"),
        ("format", "formats storage"),
    ];

    /// <summary>What the command would do, when one of its words is a risky verb; else null.</summary>
    public static string? Describe(string command)
    {
        // Every piece of a pipeline or command list counts: "ls; rm -rf x" is risky.
        foreach (var part in command.Split([';', '|', '&', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var words = part.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) continue;
            var start = words[0] is "su" or "sudo" or "toybox" or "busybox" && words.Length > 1 ? 1 : 0;
            var lead = string.Join(' ', words.Skip(start).Take(3));
            foreach (var (verb, why) in Verbs)
            {
                if (lead == verb || lead.StartsWith(verb + " ", StringComparison.Ordinal)) return why;
            }
        }
        return null;
    }
}

/// <summary>The Console's ↑/↓ history: newest last, no repeats in a row, the line being typed kept.</summary>
public sealed class CommandHistory(int max = 200)
{
    private readonly List<string> _items = [];
    private int _cursor; // == _items.Count while not walking
    private string _draft = "";

    public IReadOnlyList<string> Items => _items;

    public void Add(string command)
    {
        if (command.Length > 0 && (_items.Count == 0 || _items[^1] != command)) _items.Add(command);
        if (_items.Count > max) _items.RemoveAt(0);
        _cursor = _items.Count;
        _draft = "";
    }

    /// <summary>↑: the command before; null at the oldest (leave the box alone).</summary>
    public string? Previous(string current)
    {
        if (_cursor == 0 || _items.Count == 0) return null;
        if (_cursor == _items.Count) _draft = current;
        return _items[--_cursor];
    }

    /// <summary>↓: the command after, then back to what was being typed; null when not walking.</summary>
    public string? Next()
    {
        if (_cursor >= _items.Count) return null;
        _cursor++;
        return _cursor == _items.Count ? _draft : _items[_cursor];
    }
}

/// <summary>The Console's output, kept under a size by dropping the oldest whole lines.</summary>
public sealed class ConsoleBuffer(int maxChars = 200_000)
{
    private readonly StringBuilder _text = new();

    public string Text => _text.ToString();

    public void Append(string text)
    {
        _text.Append(text);
        if (_text.Length <= maxChars) return;
        var cut = _text.Length - maxChars;
        var s = _text.ToString();
        var newline = s.IndexOf('\n', cut);
        _text.Remove(0, newline >= 0 ? newline + 1 : cut);
    }

    public void Clear() => _text.Clear();
}
