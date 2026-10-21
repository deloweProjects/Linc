using Linc.Desktop.Services.Tools;

namespace Linc.ToolSim;

internal static class TypeRescueTests
{
    public static void Run(Action<bool, string> check)
    {
        // ---- Type: keys -----------------------------------------------------------------------
        check(TypeCommands.KeyCodeFor("Back") == 67 && TypeCommands.KeyCodeFor("Enter") == 66 &&
              TypeCommands.KeyCodeFor("Tab") == 61 && TypeCommands.KeyCodeFor("Escape") == 111 &&
              TypeCommands.KeyCodeFor("Delete") == 112, "Backspace, Enter, Tab, Esc and Delete map to their Android codes");
        check(TypeCommands.KeyCodeFor("Up") == 19 && TypeCommands.KeyCodeFor("Down") == 20 &&
              TypeCommands.KeyCodeFor("Left") == 21 && TypeCommands.KeyCodeFor("Right") == 22, "arrows map to the D-pad codes");
        check(TypeCommands.KeyCodeFor("A") is null && TypeCommands.KeyCodeFor("Shift") is null, "letters and modifiers aren't key codes");

        // ---- Type: a burst of live keys is one command, in typed order --------------------------
        var burst = TypeCommands.LiveCommand([LiveKey.Char('h'), LiveKey.Char('i'), LiveKey.Char(' '), LiveKey.Char('x'),
            LiveKey.Key(67), LiveKey.Key(67), LiveKey.Char('!'), LiveKey.Key(66)]);
        check(burst == "input text 'hi%sx' && input keyevent 67 67 && input text '!' && input keyevent 66",
            $"live keys batch neighbouring characters and key codes ({burst})");
        check(TypeCommands.LiveCommand([LiveKey.Char('é')]) is null, "a burst of only untypeable characters sends nothing");
        check(TypeCommands.LiveCommand([]) is null, "an empty burst sends nothing");
        check(TypeCommands.LiveCommand([LiveKey.Char('\'')]) == "input text ''\\'''", "a typed quote stays one shell word");

        // ---- Type: a line ----------------------------------------------------------------------
        var line = TypeCommands.LineCommand("1234", true, out var dropped);
        check(line == "input text '1234' && input keyevent 66" && !dropped, "a PIN line is typed then Enter");
        var noEnter = TypeCommands.LineCommand("hello there", false, out _);
        check(noEnter == "input text 'hello%sthere'", "without Enter after, only the text goes");
        var accented = TypeCommands.LineCommand("café", false, out var droppedAccent);
        check(accented == "input text 'caf'" && droppedAccent, "an accented character is left out and reported");
        check(TypeCommands.LineCommand("", false, out _) is null, "an empty line with no Enter sends nothing");
        check(TypeCommands.LineCommand("", true, out _) == "input keyevent 66", "an empty line with Enter just presses Enter");

        // ---- Type: unlock swipe ----------------------------------------------------------------
        check(TypeCommands.ParseScreenSize("Physical size: 1080x2400") == (1080, 2400), "wm size: the physical size");
        check(TypeCommands.ParseScreenSize("Physical size: 1440x3120\nOverride size: 1080x2340") == (1080, 2340), "wm size: an override wins");
        check(TypeCommands.ParseScreenSize("error: no display") is null, "wm size: nothing usable gives null");
        check(TypeCommands.SwipeUp(1080, 2400) == "input swipe 540 1920 540 600 250", "the unlock swipe goes from 80% to 25% up the middle");

        // ---- Rescue: the backup listing --------------------------------------------------------
        var list = BackupPlan.ListCommand();
        check(list.StartsWith("find '/sdcard/DCIM' '/sdcard/Pictures' '/sdcard/Download' '/sdcard/Documents' '/sdcard/Movies' '/sdcard/Music' -type f", StringComparison.Ordinal)
              && list.Contains("stat -c '%s|%Y|%n' {} +", StringComparison.Ordinal) && list.EndsWith("2>/dev/null", StringComparison.Ordinal),
            "one find over the six folders, size|mtime|path per file");
        var listing = string.Join("\n",
            "2483712|1729500000|/sdcard/DCIM/Camera/PXL_20241021_101500.jpg",
            "find: '/sdcard/Music': No such file or directory",
            "1024|1729400000|/sdcard/Download/notes|draft.txt\r",
            "",
            "5|1729400000|/sdcard/DCIM/.thumbnails/1.jpg",
            "9|1729400000|/sdcard/Pictures/.trashed-1730000000-old.jpg",
            "9|1729400000|/sdcard/Pictures/.pending-1730000000-new.jpg",
            "x|1|/sdcard/DCIM/bad.jpg",
            "7|1|/data/secret");
        var files = BackupPlan.ParseListing(listing);
        check(files.Count == 2, $"the listing keeps real files and skips errors, caches, trashed and pending files ({files.Count})");
        check(files.Count == 2 && files[0] == new BackupFile("/sdcard/DCIM/Camera/PXL_20241021_101500.jpg", 2483712, 1729500000),
            "size, time and path are read");
        check(files.Count == 2 && files[1].RemotePath == "/sdcard/Download/notes|draft.txt", "a | in a file name survives, a Windows line end doesn't");

        // ---- Rescue: where each file goes ------------------------------------------------------
        var sep = Path.DirectorySeparatorChar;
        check(BackupPlan.LocalRelativePath("/sdcard/DCIM/Camera/a.jpg") == $"DCIM{sep}Camera{sep}a.jpg", "folders are kept");
        check(BackupPlan.LocalRelativePath("/sdcard/Download/what?.pdf") == $"Download{sep}what_.pdf", "characters Windows can't have become _");
        check(BackupPlan.LocalRelativePath("/sdcard/Download/notes.") == $"Download{sep}notes", "a trailing dot Windows would drop is dropped");
        check(BackupPlan.LocalRelativePath("/sdcard/../etc/passwd") is null, "a path that climbs out is refused");
        check(BackupPlan.LocalRelativePath("/data/x") is null, "a path outside /sdcard is refused");

        // ---- Rescue: what's already copied -----------------------------------------------------
        var photo = new BackupFile("/sdcard/DCIM/a.jpg", 100, 1729500000);
        var when = DateTimeOffset.FromUnixTimeSeconds(1729500000).UtcDateTime;
        check(BackupPlan.AlreadyCopied(100, when, photo), "same size and time: skipped");
        check(BackupPlan.AlreadyCopied(100, when.AddSeconds(1), photo), "a second off (FAT, rounding): still skipped");
        check(!BackupPlan.AlreadyCopied(99, when, photo), "a different size: copied again");
        check(!BackupPlan.AlreadyCopied(100, when.AddMinutes(5), photo), "a different time: copied again");
        check(BackupPlan.Progress(2140, 5610) == "2,140 of 5,610 files", "progress reads like the mockup");
    }
}
