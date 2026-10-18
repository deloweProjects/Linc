using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Linc.Desktop.Protocol;
using Microsoft.Win32;

namespace Linc.Desktop.Services;

/// <summary>
/// The phone's full-page PC tools (v27, D-097): the PC's look (light/dark, accent colour,
/// transparency, wallpaper), what it is running and how it is doing, links opened on it, its
/// files (browse, download, upload, new folder, rename, delete), and a terminal (<see cref="PcTerminal"/>).
///
/// <para>The phone is the PC's own paired, trusted device — it already drives the mouse and
/// keyboard — so these act as the signed-in user, with nothing elevated. Every action is logged.</para>
/// </summary>
public sealed class PcToolsService(IConnectionManager connection, ILogService log)
{
    public const int TerminalChannel = 9;
    public const int FilesChannel = 10;

    private bool _attached;
    private PcTerminal? _terminal;

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        connection.CompanionMessageReceived += OnMessage;
    }

    private static readonly HashSet<string> Handled =
    [
        MessageType.PcLookGet, MessageType.PcLookSet, MessageType.PcWallpaperSet, MessageType.PcInfoGet, MessageType.PcAppsGet,
        MessageType.PcAppsEnd, MessageType.PcOpen, MessageType.PcFilesList, MessageType.PcFilesOp, MessageType.PcTermGet, MessageType.PcTermOpen,
    ];

    private void OnMessage(Envelope envelope)
    {
        // Every message from the phone passes here (pointer moves at 100+ a second), so only
        // this service's own are copied off the receive loop.
        if (!Handled.Contains(envelope.Type)) return;
        var p = envelope.Payload.DeepClone().AsObject();
        switch (envelope.Type)
        {
            case MessageType.PcLookGet: _ = Task.Run(() => Reply(MessageType.PcLookState, PcLook.State())); break;
            case MessageType.PcLookSet: _ = Task.Run(() => Reply(MessageType.PcLookState, PcLook.Apply(p, log))); break;
            case MessageType.PcWallpaperSet: _ = Task.Run(() => Reply(MessageType.PcLookState, PcLook.SetWallpaper(p, log))); break;
            case MessageType.PcInfoGet: _ = Task.Run(() => Reply(MessageType.PcInfo, PcInfo.Snapshot())); break;
            case MessageType.PcAppsGet: _ = Task.Run(() => Reply(MessageType.PcApps, PcInfo.Apps())); break;
            case MessageType.PcAppsEnd: _ = Task.Run(async () => { await PcInfo.EndAsync(p, log); await Reply(MessageType.PcApps, PcInfo.Apps()); }); break;
            case MessageType.PcOpen: _ = Task.Run(() => Open(p)); break;
            case MessageType.PcFilesList: _ = Task.Run(() => Reply(MessageType.PcFilesListing, PcFilesRemote.List((string?)p["path"]))); break;
            case MessageType.PcFilesOp: _ = Task.Run(() => FileOpAsync(p)); break;
            case MessageType.PcTermGet: _ = Task.Run(() => Reply(MessageType.PcTermState, PcTerminal.State(_terminal))); break;
            case MessageType.PcTermOpen: _ = Task.Run(() => OpenTerminalAsync(p)); break;
        }
    }

    private async Task Reply(string type, JsonObject payload)
    {
        try { await connection.SendToPhoneAsync(Envelope.Create(type, payload), CancellationToken.None); }
        catch (Exception ex) { log.Log(LogLevel.Warn, $"PC tools: couldn't answer the phone ({ex.Message})"); }
    }

    /// <summary>A link or a file opened on the PC, as if double-clicked.</summary>
    private void Open(JsonObject p)
    {
        string? target = (string?)p["url"] ?? (string?)p["path"];
        if (string.IsNullOrWhiteSpace(target)) return;
        bool isUrl = Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto";
        if (!isUrl && !File.Exists(target) && !Directory.Exists(target)) return;
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            log.Log(LogLevel.Info, isUrl ? "PC tools: opened a link from the phone" : $"PC tools: opened {Path.GetFileName(target)} from the phone");
        }
        catch (Exception ex) { log.Log(LogLevel.Warn, $"PC tools: couldn't open that ({ex.Message})"); }
    }

    // ---- files -------------------------------------------------------------------------------

    private async Task FileOpAsync(JsonObject p)
    {
        string op = (string?)p["op"] ?? "";
        string id = (string?)p["id"] ?? "";
        var done = new JsonObject { ["op"] = op, ["id"] = id };
        try
        {
            switch (op)
            {
                case "mkdir": done["path"] = PcFilesRemote.MakeFolder((string?)p["path"], (string?)p["name"]); break;
                case "rename": done["path"] = PcFilesRemote.Rename((string?)p["path"], (string?)p["name"]); break;
                case "delete": PcFilesRemote.Recycle((string?)p["path"]); break;
                case "open": Open(new JsonObject { ["path"] = (string?)p["path"] }); break;
                case "download": await DownloadAsync((string?)p["path"], id); break;
                case "upload": done["path"] = await UploadAsync((string?)p["dir"], (string?)p["name"], (long?)p["size"] ?? -1, id); break;
                default: throw new LincException("That isn't something the PC can do with a file.");
            }
            done["ok"] = true;
            log.Log(LogLevel.Info, $"PC tools: {op} {Path.GetFileName((string?)p["path"] ?? (string?)p["name"] ?? "")} from the phone");
        }
        catch (Exception ex)
        {
            done["ok"] = false;
            done["error"] = ex is LincException ? ex.Message : PcFilesRemote.Plain(ex);
            log.Log(LogLevel.Warn, $"PC tools: {op} failed: {ex.Message}");
        }
        await Reply(MessageType.PcFilesDone, done);
    }

    /// <summary>PC → phone: channel 10, a JSON header ({id, op, name, size}), then the bytes.</summary>
    private async Task DownloadAsync(string? path, string id)
    {
        if (path is null || !File.Exists(path)) throw new LincException("That file isn't there any more.");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
        await using var channel = await connection.OpenPhoneDialledChannelAsync(FilesChannel, CancellationToken.None);
        await Framing.WriteAsync(channel, new JsonObject { ["id"] = id, ["op"] = "download", ["name"] = Path.GetFileName(path), ["size"] = file.Length }.ToJsonString(), CancellationToken.None);
        await file.CopyToAsync(channel, 1 << 16);
        await channel.FlushAsync();
    }

    /// <summary>Phone → PC: channel 10, a JSON header from the PC ({id, op}), then the phone's bytes.</summary>
    private async Task<string> UploadAsync(string? dir, string? name, long size, string id)
    {
        if (dir is null || !Directory.Exists(dir)) throw new LincException("That folder isn't there any more.");
        string target = PcFilesRemote.UniquePath(dir, PcFilesRemote.SafeName(name));
        await using var channel = await connection.OpenPhoneDialledChannelAsync(FilesChannel, CancellationToken.None);
        await Framing.WriteAsync(channel, new JsonObject { ["id"] = id, ["op"] = "upload" }.ToJsonString(), CancellationToken.None);
        string partial = target + ".linc-part";
        try
        {
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long left = size;
                while (left != 0)
                {
                    int n = await channel.ReadAsync(buffer.AsMemory(0, (int)(left < 0 ? buffer.Length : Math.Min(buffer.Length, left))));
                    if (n == 0)
                    {
                        if (size < 0) break;
                        throw new LincException("The phone stopped sending before the file was complete.");
                    }
                    await file.WriteAsync(buffer.AsMemory(0, n));
                    if (left > 0) left -= n;
                }
            }
            File.Move(partial, target);
            return target;
        }
        finally { try { if (File.Exists(partial)) File.Delete(partial); } catch { } }
    }

    // ---- terminal ----------------------------------------------------------------------------

    private async Task OpenTerminalAsync(JsonObject p)
    {
        string shell = (string?)p["shell"] ?? "powershell";
        int cols = Math.Clamp((int?)p["cols"] ?? 80, 20, 400);
        int rows = Math.Clamp((int?)p["rows"] ?? 30, 5, 200);
        try
        {
            _terminal?.Dispose();
            var channel = await connection.OpenPhoneDialledChannelAsync(TerminalChannel, CancellationToken.None);
            _terminal = PcTerminal.Start(shell, cols, rows, channel, log);
        }
        catch (Exception ex)
        {
            log.Log(LogLevel.Warn, $"PC tools: the terminal couldn't start ({ex.Message})");
            await Reply(MessageType.PcTermState, new JsonObject { ["error"] = ex is LincException ? ex.Message : "The terminal couldn't start on the PC." });
        }
    }
}

/// <summary>Windows' light/dark, accent colour, transparency and wallpaper, as Settings → Personalisation sets them.</summary>
internal static class PcLook
{
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string Dwm = @"Software\Microsoft\Windows\DWM";
    private const string Accent = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
    private const string Desktop = @"Control Panel\Desktop";

    public static JsonObject State()
    {
        using var personalize = Registry.CurrentUser.OpenSubKey(Personalize);
        using var dwm = Registry.CurrentUser.OpenSubKey(Dwm);
        using var desktop = Registry.CurrentUser.OpenSubKey(Desktop);
        int abgr = dwm?.GetValue("AccentColor") is int a ? a : unchecked((int)0xFFD77800);
        return new JsonObject
        {
            ["appsDark"] = (personalize?.GetValue("AppsUseLightTheme") as int? ?? 1) == 0,
            ["systemDark"] = (personalize?.GetValue("SystemUsesLightTheme") as int? ?? 0) == 0,
            ["transparency"] = (personalize?.GetValue("EnableTransparency") as int? ?? 1) == 1,
            ["accent"] = $"#{abgr & 0xFF:X2}{(abgr >> 8) & 0xFF:X2}{(abgr >> 16) & 0xFF:X2}",
            ["autoAccent"] = (desktop?.GetValue("AutoColorization") as string) == "1",
            ["wallpaper"] = (desktop?.GetValue("WallPaper") as string) is { Length: > 0 } w ? Path.GetFileName(w) : null,
        };
    }

    public static JsonObject Apply(JsonObject p, ILogService log)
    {
        using (var personalize = Registry.CurrentUser.CreateSubKey(Personalize))
        {
            if ((bool?)p["appsDark"] is { } apps) personalize.SetValue("AppsUseLightTheme", apps ? 0 : 1, RegistryValueKind.DWord);
            if ((bool?)p["systemDark"] is { } system) personalize.SetValue("SystemUsesLightTheme", system ? 0 : 1, RegistryValueKind.DWord);
            if ((bool?)p["transparency"] is { } glass) personalize.SetValue("EnableTransparency", glass ? 1 : 0, RegistryValueKind.DWord);
        }
        if ((bool?)p["autoAccent"] is { } auto)
        {
            using var desktop = Registry.CurrentUser.CreateSubKey(Desktop);
            desktop.SetValue("AutoColorization", auto ? "1" : "0", RegistryValueKind.String);
        }
        if ((string?)p["accent"] is { } hex && TryParseColor(hex, out byte r, out byte g, out byte b)) SetAccent(r, g, b);
        Broadcast("ImmersiveColorSet");
        log.Log(LogLevel.Info, "PC tools: the PC's look changed from the phone");
        return State();
    }

    /// <summary>
    /// The accent everywhere Windows keeps it: DWM's two copies (title bars, borders) and the
    /// Start/taskbar palette — eight shades, lightest to darkest — which is what Settings writes.
    /// </summary>
    private static void SetAccent(byte r, byte g, byte b)
    {
        int abgr = unchecked((int)(0xFF000000u | (uint)(b << 16) | (uint)(g << 8) | r));
        int argb = unchecked((int)(0xC4000000u | (uint)(r << 16) | (uint)(g << 8) | b));
        using (var dwm = Registry.CurrentUser.CreateSubKey(Dwm))
        {
            dwm.SetValue("AccentColor", abgr, RegistryValueKind.DWord);
            dwm.SetValue("ColorizationColor", argb, RegistryValueKind.DWord);
            dwm.SetValue("ColorizationAfterglow", argb, RegistryValueKind.DWord);
        }
        using var accent = Registry.CurrentUser.CreateSubKey(Accent);
        var palette = new byte[32];
        double[] shades = [0.6, 0.4, 0.2, 0, -0.2, -0.4, -0.6, -0.75];
        for (int i = 0; i < 8; i++)
        {
            (byte sr, byte sg, byte sb) = Shade(r, g, b, shades[i]);
            palette[i * 4] = sr; palette[i * 4 + 1] = sg; palette[i * 4 + 2] = sb; palette[i * 4 + 3] = 0;
        }
        accent.SetValue("AccentPalette", palette, RegistryValueKind.Binary);
        accent.SetValue("AccentColorMenu", abgr, RegistryValueKind.DWord);
        (byte mr, byte mg, byte mb) = Shade(r, g, b, -0.4);
        accent.SetValue("StartColorMenu", unchecked((int)(0xFF000000u | (uint)(mb << 16) | (uint)(mg << 8) | mr)), RegistryValueKind.DWord);
    }

    /// <summary>Towards white (+) or black (−).</summary>
    private static (byte, byte, byte) Shade(byte r, byte g, byte b, double amount)
    {
        byte Mix(byte c) => (byte)Math.Clamp(amount >= 0 ? c + (255 - c) * amount : c * (1 + amount), 0, 255);
        return (Mix(r), Mix(g), Mix(b));
    }

    public static bool TryParseColor(string hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        hex = hex.TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int v)) return false;
        r = (byte)(v >> 16); g = (byte)(v >> 8); b = (byte)v;
        return true;
    }

    /// <summary>A photo from the phone as the desktop background ({jpeg: base64, fit}).</summary>
    public static JsonObject SetWallpaper(JsonObject p, ILogService log)
    {
        var state = State();
        if ((string?)p["jpeg"] is not { Length: > 0 } b64) return state;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); }
        catch { state["error"] = "That picture didn't arrive whole."; return state; }
        string folder = Path.Combine(PcSpeakerMode.StoreRoot ?? Path.GetTempPath(), "wallpaper");
        Directory.CreateDirectory(folder);
        // A new name each time: Windows caches by path and would keep showing the old picture.
        foreach (var old in Directory.GetFiles(folder, "phone-*.jpg")) { try { File.Delete(old); } catch { } }
        string path = Path.Combine(folder, $"phone-{DateTime.UtcNow.Ticks}.jpg");
        File.WriteAllBytes(path, bytes);
        (string style, string tile) = ((string?)p["fit"]) switch
        {
            "fit" => ("6", "0"),
            "stretch" => ("2", "0"),
            "center" => ("0", "0"),
            "tile" => ("0", "1"),
            "span" => ("22", "0"),
            _ => ("10", "0"), // fill
        };
        using (var desktop = Registry.CurrentUser.CreateSubKey(Desktop))
        {
            desktop.SetValue("WallpaperStyle", style, RegistryValueKind.String);
            desktop.SetValue("TileWallpaper", tile, RegistryValueKind.String);
        }
        if (!SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE))
        {
            state["error"] = "Windows didn't take the picture.";
            return state;
        }
        log.Log(LogLevel.Info, $"PC tools: new wallpaper from the phone ({bytes.Length / 1024} KB)");
        return State();
    }

    private static void Broadcast(string area) =>
        SendMessageTimeout(new IntPtr(0xFFFF), WM_SETTINGCHANGE, IntPtr.Zero, area, SMTO_ABORTIFHUNG, 1000, out _);

    private const int SPI_SETDESKWALLPAPER = 0x14;
    private const int SPIF_UPDATEINIFILE = 0x1;
    private const int SPIF_SENDCHANGE = 0x2;
    private const int WM_SETTINGCHANGE = 0x1A;
    private const int SMTO_ABORTIFHUNG = 0x2;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(int action, int param, string value, int winIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam, string lParam, int flags, int timeout, out IntPtr result);
}

/// <summary>How the PC is doing, and what it is running.</summary>
internal static class PcInfo
{
    public static JsonObject Snapshot()
    {
        var info = new JsonObject
        {
            ["name"] = Environment.MachineName,
            ["user"] = Environment.UserName,
            ["os"] = RuntimeInformation.OSDescription,
            ["uptimeMin"] = Environment.TickCount64 / 60_000,
            ["cpuPct"] = CpuPercent(),
        };
        var memory = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref memory))
        {
            info["memTotalMb"] = (long)(memory.ullTotalPhys / (1024 * 1024));
            info["memUsedMb"] = (long)((memory.ullTotalPhys - memory.ullAvailPhys) / (1024 * 1024));
        }
        var disks = new JsonArray();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
            disks.Add(new JsonObject { ["name"] = d.Name.TrimEnd('\\'), ["label"] = d.VolumeLabel, ["freeGb"] = Math.Round(d.AvailableFreeSpace / 1e9, 1), ["totalGb"] = Math.Round(d.TotalSize / 1e9, 1) });
        info["disks"] = disks;
        if (GetSystemPowerStatus(out var power) && power.BatteryFlag != 128 && power.BatteryLifePercent <= 100)
            info["battery"] = new JsonObject { ["percent"] = power.BatteryLifePercent, ["charging"] = power.ACLineStatus == 1 };
        return info;
    }

    /// <summary>The CPU over a quarter of a second, from the system's idle/kernel/user times.</summary>
    private static int CpuPercent()
    {
        if (!GetSystemTimes(out long idle1, out long kernel1, out long user1)) return -1;
        Thread.Sleep(250);
        if (!GetSystemTimes(out long idle2, out long kernel2, out long user2)) return -1;
        long total = (kernel2 - kernel1) + (user2 - user1); // kernel time includes idle
        long busy = total - (idle2 - idle1);
        return total > 0 ? (int)Math.Round(100.0 * busy / total) : 0;
    }

    /// <summary>Apps with a window, as Task Manager's "Apps" list shows them.</summary>
    public static JsonObject Apps()
    {
        var list = new JsonArray();
        foreach (var process in Process.GetProcesses().OrderByDescending(p => SafeMemory(p)))
        {
            try
            {
                if (process.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(process.MainWindowTitle)) continue;
                if (process.Id == Environment.ProcessId) continue;
                list.Add(new JsonObject
                {
                    ["pid"] = process.Id,
                    ["name"] = process.ProcessName,
                    ["title"] = process.MainWindowTitle,
                    ["memMb"] = SafeMemory(process) / (1024 * 1024),
                });
            }
            catch { /* it exited while being looked at */ }
            finally { process.Dispose(); }
        }
        return new JsonObject { ["apps"] = list };
    }

    private static long SafeMemory(Process p) { try { return p.WorkingSet64; } catch { return 0; } }

    /// <summary>Ask the app to close, as its window's X would; if it is still there after 3 s, end it.</summary>
    public static async Task EndAsync(JsonObject p, ILogService log)
    {
        if ((int?)p["pid"] is not { } pid || pid == Environment.ProcessId) return;
        try
        {
            using var process = Process.GetProcessById(pid);
            string name = process.ProcessName;
            process.CloseMainWindow();
            for (int i = 0; i < 30 && !process.HasExited; i++) await Task.Delay(100);
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            log.Log(LogLevel.Info, $"PC tools: closed {name} from the phone");
        }
        catch (Exception ex) { log.Log(LogLevel.Warn, $"PC tools: couldn't close that app ({ex.Message})"); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
}

/// <summary>The PC's files, for the phone: the same reach the signed-in user has in Explorer.</summary>
internal static class PcFilesRemote
{
    private const int MaxEntries = 2000;

    public static JsonObject List(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Roots();
        var result = new JsonObject { ["path"] = path };
        try
        {
            var dir = new DirectoryInfo(path);
            if (!dir.Exists) throw new LincException("That folder isn't there any more.");
            result["parent"] = dir.Parent?.FullName ?? "";
            var entries = new JsonArray();
            int count = 0;
            foreach (var item in dir.EnumerateFileSystemInfos()
                         .Where(i => (i.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                         .OrderByDescending(i => i is DirectoryInfo).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (++count > MaxEntries) { result["truncated"] = true; break; }
                entries.Add(new JsonObject
                {
                    ["name"] = item.Name,
                    ["path"] = item.FullName,
                    ["dir"] = item is DirectoryInfo,
                    ["size"] = item is FileInfo f ? f.Length : 0,
                    ["modified"] = new DateTimeOffset(item.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                });
            }
            result["entries"] = entries;
        }
        catch (Exception ex)
        {
            result["entries"] = new JsonArray();
            result["error"] = ex is LincException ? ex.Message : Plain(ex);
        }
        return result;
    }

    /// <summary>The places Explorer starts from: the user's folders, then every drive.</summary>
    private static JsonObject Roots()
    {
        var entries = new JsonArray();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var (name, path) in new[]
                 {
                     ("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
                     ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
                     ("Downloads", Path.Combine(home, "Downloads")),
                     ("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
                     ("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
                     ("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
                 })
        {
            if (Directory.Exists(path)) entries.Add(new JsonObject { ["name"] = name, ["path"] = path, ["dir"] = true, ["place"] = true });
        }
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel;
            entries.Add(new JsonObject
            {
                ["name"] = $"{label} ({d.Name.TrimEnd('\\')})",
                ["path"] = d.RootDirectory.FullName,
                ["dir"] = true,
                ["drive"] = true,
                ["freeGb"] = Math.Round(d.AvailableFreeSpace / 1e9, 1),
                ["totalGb"] = Math.Round(d.TotalSize / 1e9, 1),
            });
        }
        return new JsonObject { ["path"] = "", ["entries"] = entries };
    }

    public static string MakeFolder(string? parent, string? name)
    {
        if (parent is null || !Directory.Exists(parent)) throw new LincException("That folder isn't there any more.");
        string path = UniquePath(parent, SafeName(name));
        Directory.CreateDirectory(path);
        return path;
    }

    public static string Rename(string? path, string? name)
    {
        if (path is null) throw new LincException("Nothing to rename.");
        string parent = Path.GetDirectoryName(path.TrimEnd('\\')) ?? throw new LincException("A drive can't be renamed here.");
        string target = Path.Combine(parent, SafeName(name));
        if (string.Equals(target, path, StringComparison.OrdinalIgnoreCase)) return path;
        if (File.Exists(target) || Directory.Exists(target)) throw new LincException("Something with that name is already there.");
        if (Directory.Exists(path)) Directory.Move(path, target);
        else if (File.Exists(path)) File.Move(path, target);
        else throw new LincException("That isn't there any more.");
        return target;
    }

    /// <summary>To the Recycle Bin, never gone for good: it can be restored on the PC.</summary>
    public static void Recycle(string? path)
    {
        if (path is null || (!File.Exists(path) && !Directory.Exists(path))) throw new LincException("That isn't there any more.");
        if (Path.GetPathRoot(path)?.TrimEnd('\\') == path.TrimEnd('\\')) throw new LincException("A whole drive can't be deleted.");
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        int hr = SHFileOperation(ref op);
        if (hr != 0 || op.fAnyOperationsAborted) throw new LincException("Windows couldn't move that to the Recycle Bin (it may be open).");
    }

    public static string SafeName(string? name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            throw new LincException("That name has characters Windows doesn't allow (\\ / : * ? \" < > |).");
        return name;
    }

    public static string UniquePath(string dir, string name)
    {
        string path = Path.Combine(dir, name);
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int i = 2; ; i++)
        {
            path = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
        }
    }

    public static string Plain(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Windows doesn't let your account do that there.",
        IOException io when io.HResult == unchecked((int)0x80070020) => "That file is open in another app.",
        IOException => "Windows couldn't do that: " + ex.Message,
        _ => "That didn't work on the PC.",
    };

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
}
