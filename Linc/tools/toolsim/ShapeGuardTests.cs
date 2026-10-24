using System.Diagnostics;
using System.Text;
using Linc.Desktop.Services;

namespace Linc.ToolSim;

/// <summary>"Fill this monitor"'s safety net on the phone (PhoneShapeGuard): the restore lines, the
/// install command, and, where a POSIX shell exists (not on the Windows runner), the watcher
/// itself run against fake phone commands.</summary>
internal static class ShapeGuardTests
{
    public static void Run(Action<bool, string> check)
    {
        var restore = PhoneShapeGuard.RestoreCommands("1080x2400", true, "1", "0");
        check(restore == "wm size 1080x2400\nsettings put system accelerometer_rotation 1\nsettings put system user_rotation 0\nsettings delete global linc_shape_active\n",
            "restore puts back the earlier size and rotation, and clears the phone's card");
        check(PhoneShapeGuard.RestoreCommands(null, false, null, null) == "wm size reset\nsettings delete global linc_shape_active\n",
            "no earlier size: reset; not sideways: rotation untouched");
        check(PhoneShapeGuard.RestoreCommands("1080x2400; rm -rf /", true, "0", "9").StartsWith("wm size reset\nsettings put system accelerometer_rotation 0\nsettings delete system user_rotation\n", StringComparison.Ordinal),
            "a size or rotation that isn't one is never written into the script");
        var install = PhoneShapeGuard.InstallCommand(restore);
        var b64 = install.Split(' ').Where(w => w.Length > 40 && !w.Contains('/')).ToList();
        check(b64.Count == 2 && Encoding.UTF8.GetString(Convert.FromBase64String(b64[0])) == restore
              && Encoding.UTF8.GetString(Convert.FromBase64String(b64[1])) == PhoneShapeGuard.WatchScript(),
            "the install command carries exactly the restore lines and the watcher");
        check(install.Contains("nohup setsid sh " + PhoneShapeGuard.ScriptPath) && install.StartsWith("settings put global linc_shape_active 1;", StringComparison.Ordinal),
            "the watcher is started detached, and the phone's card is switched on first");
        check(PhoneShapeGuard.DisarmCommand.Contains("rm -f " + PhoneShapeGuard.RestorePath) && PhoneShapeGuard.DisarmCommand.Contains("linc_shape_active"),
            "the PC's own restore ends the watcher and the card");

        if (!File.Exists("/bin/sh"))
        {
            Console.WriteLine("    (no POSIX shell here: the watcher's run is checked on Linux)");
            return;
        }
        var root = Path.Combine(Path.GetTempPath(), "linc-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        try
        {
            void Fake(string name, string body)
            {
                var f = Path.Combine(root, "bin", name);
                File.WriteAllText(f, "#!/bin/sh\n" + body.Replace("$R", root) + "\n");
                Process.Start("chmod", ["+x", f])!.WaitForExit();
            }
            Fake("settings", "f=$R/set_$2_$3\ncase \"$1\" in get) [ -f \"$f\" ] && cat \"$f\" || echo null;; put) echo \"$4\" > \"$f\"; echo \"settings put $2 $3 $4\" >> $R/log;; delete) rm -f \"$f\"; echo \"settings delete $2 $3\" >> $R/log;; esac");
            Fake("pgrep", "[ -f $R/scrcpy ]");
            Fake("input", "echo \"input $*\" >> $R/log");
            Fake("wm", "echo \"wm $*\" >> $R/log");
            Fake("sleep", "/bin/sleep 0.05");
            var script = PhoneShapeGuard.WatchScript().Replace("/data/local/tmp", root);
            File.WriteAllText(Path.Combine(root, "watch.sh"), script);
            var marker = Path.Combine(root, "linc-shape.restore");
            var log = Path.Combine(root, "log");

            Process Start()
            {
                var info = new ProcessStartInfo("/bin/sh", [Path.Combine(root, "watch.sh")]) { UseShellExecute = false };
                info.Environment["PATH"] = Path.Combine(root, "bin") + ":" + Environment.GetEnvironmentVariable("PATH");
                return Process.Start(info)!;
            }
            void Reset() { File.Delete(log); File.WriteAllText(marker, restore); File.WriteAllText(Path.Combine(root, "scrcpy"), ""); }

            Reset();
            var p = Start();
            Thread.Sleep(400);
            check(!p.HasExited && File.Exists(marker), "while the mirror runs, the watcher waits");
            File.Delete(Path.Combine(root, "scrcpy"));
            check(p.WaitForExit(5000) && !File.Exists(marker) && File.ReadAllText(log).Contains("wm size 1080x2400\nsettings put system accelerometer_rotation 1")
                  && File.ReadAllText(log).Contains("input keyevent 224"),
                "the mirror gone and the PC silent: the phone puts its own screen back and wakes it");

            Reset();
            p = Start();
            Thread.Sleep(300);
            File.Delete(marker);
            check(p.WaitForExit(5000) && !(File.Exists(log) && File.ReadAllText(log).Contains("wm size")),
                "the PC restored it itself: the watcher ends without touching the screen");

            Reset();
            p = Start();
            Thread.Sleep(300);
            File.WriteAllText(Path.Combine(root, "set_global_linc_shape_reset"), "1");
            check(p.WaitForExit(5000) && File.ReadAllText(log).Contains("wm size 1080x2400"),
                "the phone's Reset card puts the screen back at once, even mid-mirror");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
