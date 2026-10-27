using System.Text;

namespace Linc.Desktop.Services;

/// <summary>
/// The phone-side safety net for "Fill this monitor" (owner, 2026-10-24: "I disconnected abruptly,
/// so my screen went black"). Mirroring resizes and turns the phone's display; the PC puts it back
/// when mirroring stops, but a pulled cable or a sleeping PC can't reach the phone to do it. So
/// while a shape is applied, a small shell script runs on the phone itself (started over adb with
/// setsid/nohup, so it outlives the adb connection) and puts the screen back by itself once the
/// scrcpy server it saw has been gone for <see cref="GraceSeconds"/>, or at once when the phone
/// app asks (setting <see cref="ResetSetting"/>, written by its "Reset screen" card and its
/// Settings). The PC removes <see cref="RestorePath"/> when it restores normally, and the script
/// then simply ends. Dependency-free: tools/toolsim checks the text and runs the script against fakes.
/// <para>
/// 2026-10-27, owner: "reset the screen auto when phone disconnects". The grace was 16 s, longer
/// than the owner's cable was out, so the phone stayed squashed until it was plugged back in. It is
/// 5 s now, counted only once the mirror's server has been seen: scrcpy took ~4 s to start it on
/// the owner's Pixel 7, and a short grace from the start would undo the shape before the mirror
/// came up. A mirror that never comes up still gets its screen back after <see cref="StartSeconds"/>.
/// </para>
/// </summary>
public static class PhoneShapeGuard
{
    public const string RestorePath = "/data/local/tmp/linc-shape.restore";
    public const string ScriptPath = "/data/local/tmp/linc-shape-watch.sh";
    public const string PidPath = "/data/local/tmp/linc-shape-watch.pid";
    public const string ResetSetting = "linc_shape_reset";
    /// <summary>"1" while the screen is changed: the phone app reads it to show its Reset card.</summary>
    public const string ActiveSetting = "linc_shape_active";
    public const int GraceSeconds = 5;
    public const int StartSeconds = 60;
    private const int PollSeconds = 1;

    /// <summary>The shell lines that put the screen back: the earlier size override (or none), and
    /// the rotation settings when the phone was turned sideways.</summary>
    public static string RestoreCommands(string? sizeOverride, bool landscape, string? accelerometerRotation, string? userRotation)
    {
        var sb = new StringBuilder();
        sb.Append(sizeOverride is { Length: > 0 } && IsSize(sizeOverride) ? $"wm size {sizeOverride}\n" : "wm size reset\n");
        if (landscape)
        {
            sb.Append($"settings put system accelerometer_rotation {(accelerometerRotation is "0" ? "0" : "1")}\n");
            sb.Append(userRotation is { } r && int.TryParse(r, out var n) && n is >= 0 and <= 3
                ? $"settings put system user_rotation {n}\n"
                : "settings delete system user_rotation\n");
        }
        sb.Append($"settings delete global {ActiveSetting}\n");
        return sb.ToString();
    }

    /// <summary>The watcher. One at a time (a newer one ends the older); ends quietly once the PC
    /// has removed the restore file.</summary>
    public static string WatchScript() =>
        "#!/system/bin/sh\n" +
        "# Linc: puts the phone's screen back if mirroring ends without the PC doing it.\n" +
        $"M={RestorePath}\n" +
        $"P={PidPath}\n" +
        "[ -f \"$P\" ] && kill \"$(cat \"$P\")\" 2>/dev/null\n" +
        "echo $$ > \"$P\"\n" +
        $"settings delete global {ResetSetting} >/dev/null 2>&1\n" +
        "gone=0\n" +
        "seen=0\n" +
        "while [ -f \"$M\" ]; do\n" +
        $"  if [ \"$(settings get global {ResetSetting} 2>/dev/null)\" = \"1\" ]; then gone=1000; fi\n" +
        "  if [ $gone -lt 1000 ]; then\n" +
        "    if pgrep -f com.genymobile.scrcpy.Server >/dev/null 2>&1; then gone=0; seen=1; else gone=$((gone+1)); fi\n" +
        "  fi\n" +
        $"  if [ $seen -eq 1 ]; then lim={GraceSeconds / PollSeconds}; else lim={StartSeconds / PollSeconds}; fi\n" +
        "  if [ $gone -ge $lim ]; then\n" +
        "    sh \"$M\"\n" +
        "    rm -f \"$M\"\n" +
        $"    settings delete global {ResetSetting} >/dev/null 2>&1\n" +
        "    input keyevent 224\n" +
        "    break\n" +
        "  fi\n" +
        $"  sleep {PollSeconds}\n" +
        "done\n" +
        "rm -f \"$P\"\n";

    /// <summary>One adb shell command that writes both files and starts the watcher detached.</summary>
    public static string InstallCommand(string restoreCommands) =>
        $"settings put global {ActiveSetting} 1; " +
        $"echo {B64(restoreCommands)} | base64 -d > {RestorePath} && " +
        $"echo {B64(WatchScript())} | base64 -d > {ScriptPath} && " +
        $"(nohup setsid sh {ScriptPath} </dev/null >/dev/null 2>&1 &)";

    /// <summary>What the PC runs after it has put the screen back itself: the watcher then ends.</summary>
    public const string DisarmCommand = "rm -f " + RestorePath + "; settings delete global " + ActiveSetting;

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static bool IsSize(string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d{2,5}x\d{2,5}$");
}
