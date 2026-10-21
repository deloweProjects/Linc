using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Linc.Desktop.Services.Tools;

/// <summary>One key for Live keys: a character to type, or an Android key code.</summary>
public readonly record struct LiveKey(char Character, int Code)
{
    public static LiveKey Char(char c) => new(c, 0);
    public static LiveKey Key(int code) => new('\0', code);
    public bool IsCharacter => Code == 0;
}

/// <summary>
/// Type's commands (TOOLS.md §1), kept free of WinUI so tools/toolsim proves them: which PC key
/// is which Android key code, how a line or a burst of live keys becomes one shell command, and
/// where the unlock swipe goes.
/// </summary>
public static class TypeCommands
{
    public const int Wake = 224, Home = 3, Back = 4, Recents = 187, Enter = 66, Backspace = 67, Tab = 61,
        Escape = 111, Delete = 112, Up = 19, Down = 20, Left = 21, Right = 22, Center = 23, Sleep = 223,
        PlayPause = 85, Next = 87, Previous = 88, VolumeUp = 24, VolumeDown = 25, Mute = 164, Paste = 279;

    /// <summary>The Android key code for a PC key that isn't a character (by its VirtualKey name),
    /// or null for keys Live keys leaves alone.</summary>
    public static int? KeyCodeFor(string virtualKey) => virtualKey switch
    {
        "Back" => Backspace,
        "Enter" => Enter,
        "Tab" => Tab,
        "Escape" => Escape,
        "Delete" => Delete,
        "Up" => Up,
        "Down" => Down,
        "Left" => Left,
        "Right" => Right,
        "Home" => 122,     // KEYCODE_MOVE_HOME
        "End" => 123,      // KEYCODE_MOVE_END
        _ => null,
    };

    /// <summary>
    /// One shell command for a burst of live keys: neighbouring characters go as one
    /// <c>input text</c>, neighbouring key codes as one <c>input keyevent a b c</c>, in the order
    /// typed. Null when there is nothing <c>input</c> can send.
    /// </summary>
    public static string? LiveCommand(IReadOnlyList<LiveKey> keys)
    {
        var parts = new List<string>();
        var text = new System.Text.StringBuilder();
        var codes = new List<int>();
        void FlushText()
        {
            foreach (var chunk in AdbQuote.InputTextChunks(text.ToString())) parts.Add("input text " + chunk);
            text.Clear();
        }
        void FlushCodes()
        {
            if (codes.Count > 0) parts.Add("input keyevent " + string.Join(' ', codes));
            codes.Clear();
        }
        foreach (var key in keys)
        {
            if (key.IsCharacter)
            {
                if (!AdbQuote.IsTypeable(key.Character)) continue;
                FlushCodes();
                text.Append(key.Character);
            }
            else
            {
                FlushText();
                codes.Add(key.Code);
            }
        }
        FlushText();
        FlushCodes();
        return parts.Count == 0 ? null : string.Join(" && ", parts);
    }

    /// <summary>
    /// The command for a typed line, then Enter when <paramref name="enter"/>. Null when there is
    /// nothing to send. <paramref name="dropped"/> says some characters can't go through
    /// <c>input text</c> (they need the Linc app).
    /// </summary>
    public static string? LineCommand(string text, bool enter, out bool dropped)
    {
        dropped = text.Any(c => !AdbQuote.IsTypeable(c) && c is not ('\r' or '\n'));
        var parts = AdbQuote.InputTextChunks(text).Select(chunk => "input text " + chunk).ToList();
        if (enter) parts.Add("input keyevent " + Enter);
        return parts.Count == 0 ? null : string.Join(" && ", parts);
    }

    private static readonly Regex SizeLine = new(@"(Physical|Override) size:\s*(\d+)x(\d+)", RegexOptions.Compiled);

    /// <summary>The screen size from <c>wm size</c>; an "Override size" wins over the physical one.</summary>
    public static (int Width, int Height)? ParseScreenSize(string wmSize)
    {
        (int, int)? physical = null, overridden = null;
        foreach (Match m in SizeLine.Matches(wmSize))
        {
            var size = (int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
            if (m.Groups[1].Value == "Override") overridden = size; else physical = size;
        }
        return overridden ?? physical;
    }

    /// <summary>The upward swipe that opens the lock screen's PIN pad (bottom 80% to 25%).</summary>
    public static string SwipeUp(int width, int height) =>
        $"input swipe {width / 2} {height * 8 / 10} {width / 2} {height / 4} 250";
}

/// <summary>One file Rescue's backup copies.</summary>
public sealed record BackupFile(string RemotePath, long Size, long ModifiedUnix);

/// <summary>
/// Rescue's "Save your stuff" (TOOLS.md §2): which folders, one listing command, how its output
/// is read, where each file lands on the PC and which ones are already there.
/// </summary>
public static class BackupPlan
{
    public static readonly string[] Folders = ["DCIM", "Pictures", "Download", "Documents", "Movies", "Music"];

    /// <summary>One <c>find</c> over every folder: <c>size|mtime|path</c> per file. Folders that
    /// don't exist or can't be read are skipped quietly (stderr goes nowhere).</summary>
    public static string ListCommand() =>
        "find " + string.Join(' ', Folders.Select(f => AdbQuote.Arg("/sdcard/" + f))) +
        " -type f -exec stat -c '%s|%Y|%n' {} + 2>/dev/null";

    /// <summary>Reads the listing; lines that aren't <c>size|mtime|path</c> (errors, blanks) and
    /// Android's own leftovers (thumbnail caches, trashed and pending files) are left out.</summary>
    public static List<BackupFile> ParseListing(string output)
    {
        var files = new List<BackupFile>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var parts = line.Split('|', 3);
            if (parts.Length != 3) continue;
            if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var size)) continue;
            if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var mtime)) continue;
            var path = parts[2];
            if (!path.StartsWith("/sdcard/", StringComparison.Ordinal)) continue;
            var name = path[(path.LastIndexOf('/') + 1)..];
            if (path.Contains("/.thumbnails/", StringComparison.Ordinal) ||
                name.StartsWith(".trashed-", StringComparison.Ordinal) ||
                name.StartsWith(".pending-", StringComparison.Ordinal) ||
                name.Length == 0)
            {
                continue;
            }
            files.Add(new BackupFile(path, size, mtime));
        }
        return files;
    }

    /// <summary>Where a phone file goes under the backup folder, keeping its folders
    /// (<c>/sdcard/DCIM/Camera/a.jpg</c> → <c>DCIM\Camera\a.jpg</c>). Characters Windows can't
    /// have in a name become <c>_</c>. Null for anything outside /sdcard or that climbs out.</summary>
    public static string? LocalRelativePath(string remotePath)
    {
        const string root = "/sdcard/";
        if (!remotePath.StartsWith(root, StringComparison.Ordinal)) return null;
        var segments = remotePath[root.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or "..")) return null;
        var clean = segments.Select(s =>
        {
            var chars = s.Select(c => c < ' ' || "<>:\"\\|?*".Contains(c) ? '_' : c).ToArray();
            var name = new string(chars).TrimEnd('.', ' ');
            return name.Length == 0 ? "_" : name;
        });
        return string.Join(Path.DirectorySeparatorChar, clean);
    }

    /// <summary>A copy already on the PC: the same size, and the same time to within two seconds
    /// (the backup sets each copy's time to the phone's).</summary>
    public static bool AlreadyCopied(long localSize, DateTime localModifiedUtc, BackupFile remote) =>
        localSize == remote.Size &&
        Math.Abs((localModifiedUtc - DateTimeOffset.FromUnixTimeSeconds(remote.ModifiedUnix).UtcDateTime).TotalSeconds) <= 2;

    /// <summary>"2,140 of 5,610 files".</summary>
    public static string Progress(int done, int total) =>
        string.Format(CultureInfo.InvariantCulture, "{0:N0} of {1:N0} files", done, total);
}
