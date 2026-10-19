using System.Diagnostics;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using Linc.UiShots;

// Screenshots of the real PC app with a fake phone connected (ui-test.yml).
//
//   linc-uishots --exe <published Linc.Desktop.exe> --out <dir>
//
// Runs the app against a throwaway data root (D-057: never the owner's %LOCALAPPDATA%\Linc),
// pairs it with FakePhone by writing settings.json first, then presses the app's own buttons
// through UI Automation, as a person would, and saves a PNG per page, per window size and per
// navigation style. Exits 1 if the app died or the phone never connected; a button it couldn't
// find is a note in summary.md, so one moved control doesn't hide every other screen.

SetProcessDPIAware();
string Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault()
    ?? throw new ArgumentException($"missing {name}");
var exe = Path.GetFullPath(Arg("--exe"));
var outDir = Path.GetFullPath(Arg("--out"));
Directory.CreateDirectory(outDir);

var notes = new List<string>();
var shots = new List<string>();
void Log(string line) { Console.WriteLine(line); }

var root = Path.Combine(Path.GetTempPath(), "Linc_uishots_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var cert = FakePhone.CreateCertificate(Path.Combine(root, "phone.pfx"));
WriteSettings(root, Convert.ToBase64String(cert.RawData));

var app = Process.Start(new ProcessStartInfo(exe, $"--data-root \"{root}\"") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! })!;
using var stop = new CancellationTokenSource();
var phone = new FakePhone(cert, Log);
var phoneTask = Task.Run(() => phone.RunAsync(stop.Token));
int exitCode = 0;

try
{
    var hwnd = WaitForWindow(app, TimeSpan.FromSeconds(90));
    if (hwnd == IntPtr.Zero) throw new Exception("the app never showed its window");
    Log($"window up ({app.Id})");

    var connectedBy = DateTime.UtcNow.AddSeconds(90);
    while (!phone.Connected && DateTime.UtcNow < connectedBy && !app.HasExited) Thread.Sleep(500);
    if (!phone.Connected)
    {
        notes.Add("- **The fake phone never connected.** Screens below show the PC with no phone.");
        exitCode = 1;
    }
    Thread.Sleep(8000); // status, wallpaper, apps and notifications land and get drawn

    var window = AutomationElement.FromHandle(hwnd);

    // ---- every page, in the window size Linc opens at -------------------------------------------
    Resize(hwnd, 1280, 820);
    string[] pages = ["Home", "Device", "Files", "Clipboard", "Share", "Sync", "Details", "Logs", "Settings"];
    foreach (var page in pages)
    {
        if (Press(window, page)) { Thread.Sleep(2500); Shot(hwnd, $"page-{page.ToLowerInvariant()}"); }
    }

    // ---- the floating bar's hover names --------------------------------------------------------
    if (Press(window, "Home")) Thread.Sleep(1500);
    if (Find(window, "Device") is { } device)
    {
        var r = device.Current.BoundingRectangle;
        SetCursorPos((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
        Thread.Sleep(2000);
        Shot(hwnd, "home-hover-device");
        SetCursorPos(5, 5);
    }

    // ---- Home at other window sizes ------------------------------------------------------------
    foreach (var (w, h) in new[] { (1600, 1000), (1000, 820), (760, 900) })
    {
        Resize(hwnd, w, h);
        Thread.Sleep(2500);
        Shot(hwnd, $"home-{w}x{h}");
    }
    Resize(hwnd, 1280, 820);
    Thread.Sleep(1500);

    // ---- the side rail instead (Settings › Appearance › Navigation) ----------------------------
    if (Press(window, "Settings"))
    {
        Thread.Sleep(2000);
        if (Select(window, "Side rail"))
        {
            Thread.Sleep(2000);
            Shot(hwnd, "settings-side-rail");
            if (Press(window, "Home")) { Thread.Sleep(2500); Shot(hwnd, "home-side-rail"); }
            if (Press(window, "Settings")) { Thread.Sleep(2000); Select(window, "Floating bar"); }
        }
    }

    if (app.HasExited)
    {
        notes.Add($"- **The app exited** (code {app.ExitCode}) during the run.");
        exitCode = 1;
    }
}
catch (Exception ex)
{
    notes.Add($"- **Run stopped:** {ex.GetType().Name}: {ex.Message}");
    exitCode = 1;
}
finally
{
    stop.Cancel();
    try { if (!app.HasExited) app.Kill(entireProcessTree: true); } catch { }
    try { phoneTask.Wait(3000); } catch { }
    CopyLogs(root, outDir);
    if (File.Exists(Path.Combine(outDir, "pc-log.txt"))) Console.WriteLine(File.ReadAllText(Path.Combine(outDir, "pc-log.txt")));
    WriteSummary();
    try { Directory.Delete(root, recursive: true); } catch { }
}
return exitCode;

// ---- helpers --------------------------------------------------------------------------------

void WriteSummary()
{
    var text = $"## PC screens\n\nFake phone connected: **{(phone.Connected ? "yes" : "no")}** · requests answered: {phone.Requests} · {shots.Count} screenshots\n\n" +
        (notes.Count > 0 ? "### Notes\n\n" + string.Join("\n", notes) + "\n\n" : "") +
        string.Join("\n", shots.Select(s => $"- {s}")) + "\n";
    File.WriteAllText(Path.Combine(outDir, "summary.md"), text);
    Console.WriteLine(text);
}

static void WriteSettings(string root, string certBase64)
{
    // DeviceRegistry reads these exact PascalCase names (System.Text.Json defaults); a phone with
    // its certificate pinned, DirectOnly (Preference 3), every lane on, and three folder pairs
    // (one each way) for the Sync page.
    string Folder(string name) => Directory.CreateDirectory(Path.Combine(root, "SyncDemo", name)).FullName;
    JsonObject Pair(string id, string name, string pc, string phone, string direction, bool enabled = true) => new()
    {
        ["Id"] = id, ["Name"] = name, ["PcPath"] = pc, ["PhonePath"] = phone, ["Direction"] = direction, ["Enabled"] = enabled,
    };
    var settings = new JsonObject
    {
        ["PairedSerial"] = FakePhone.Serial,
        ["PairedModel"] = FakePhone.Model,
        ["ClipboardSyncEnabled"] = true,
        ["NotificationSyncEnabled"] = true,
        ["BackgroundConnectionEnabled"] = true,
        ["UpdatesEnabled"] = false,
        ["KnownDevices"] = new JsonArray(new JsonObject
        {
            ["Serial"] = FakePhone.Serial,
            ["Model"] = FakePhone.Model,
            ["FirstPairedUtc"] = "2026-10-01T00:00:00+00:00",
            ["PhoneCertBase64"] = certBase64,
            ["SyncLanes"] = new JsonObject { ["folders"] = true, ["photos"] = true, ["messages"] = true, ["calls"] = true },
            ["FolderPairs"] = new JsonArray(
                Pair("pair-docs", "Documents", Folder("Documents"), "/sdcard/Documents", "both"),
                Pair("pair-music", "Music", Folder("Music"), "/sdcard/Music", "toPhone"),
                Pair("pair-shots", "Screenshots", Folder("Screenshots"), "/sdcard/Pictures/Screenshots", "toPc", enabled: false)),
            ["Hidden"] = false,
            ["Preference"] = 3,
        }),
    };
    File.WriteAllText(Path.Combine(root, "settings.json"), settings.ToJsonString());
}

static IntPtr WaitForWindow(Process app, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until && !app.HasExited)
    {
        app.Refresh();
        if (app.MainWindowHandle != IntPtr.Zero) return app.MainWindowHandle;
        Thread.Sleep(500);
    }
    return IntPtr.Zero;
}

AutomationElement? Find(AutomationElement window, string name)
{
    for (int i = 0; i < 6; i++)
    {
        var found = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name));
        if (found is not null) return found;
        Thread.Sleep(500);
    }
    return null;
}

bool Press(AutomationElement window, string name)
{
    var element = Find(window, name);
    if (element is null) { notes.Add($"- couldn't find **{name}** to press"); return false; }
    try
    {
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) ((InvokePattern)invoke).Invoke();
        else if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select)) ((SelectionItemPattern)select).Select();
        else { notes.Add($"- **{name}** can't be pressed through UI Automation"); return false; }
        return true;
    }
    catch (Exception ex) { notes.Add($"- pressing **{name}** failed: {ex.Message}"); return false; }
}

bool Select(AutomationElement window, string name)
{
    var element = Find(window, name);
    if (element is null) { notes.Add($"- couldn't find **{name}** to select"); return false; }
    try
    {
        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select)) ((SelectionItemPattern)select).Select();
        else if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) ((InvokePattern)invoke).Invoke();
        else return false;
        return true;
    }
    catch (Exception ex) { notes.Add($"- selecting **{name}** failed: {ex.Message}"); return false; }
}

void Shot(IntPtr hwnd, string name)
{
    SetForegroundWindow(hwnd);
    Thread.Sleep(300);
    if (DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out RECT r, Marshal.SizeOf<RECT>()) != 0) GetWindowRect(hwnd, out r);
    int w = r.Right - r.Left, h = r.Bottom - r.Top;
    if (w <= 0 || h <= 0) { notes.Add($"- {name}: the window had no size"); return; }
    using var bmp = new Bitmap(w, h);
    using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h));
    var file = $"{shots.Count + 1:00}-{name}.png";
    bmp.Save(Path.Combine(outDir, file), ImageFormat.Png);
    shots.Add(file);
    Log($"shot {file}");
}

static void Resize(IntPtr hwnd, int w, int h)
{
    ShowWindow(hwnd, 9 /* SW_RESTORE */);
    SetWindowPos(hwnd, IntPtr.Zero, 20, 20, w, h, 0x0004 /* SWP_NOZORDER */);
}

static void CopyLogs(string root, string outDir)
{
    try
    {
        var logs = Path.Combine(root, "logs");
        if (!Directory.Exists(logs)) return;
        using var output = new StreamWriter(Path.Combine(outDir, "pc-log.txt"));
        foreach (var file in Directory.GetFiles(logs, "*.ndjson").OrderBy(f => f))
            foreach (var line in File.ReadLines(file))
            {
                var node = JsonNode.Parse(line);
                output.WriteLine($"{(string?)node?["Timestamp"]} {(int?)node?["Level"]} {(string?)node?["Message"]}");
            }
        var crash = Directory.GetFiles(root, "*crash*", SearchOption.TopDirectoryOnly);
        foreach (var c in crash) File.Copy(c, Path.Combine(outDir, Path.GetFileName(c)), overwrite: true);
    }
    catch (Exception ex) { Console.WriteLine($"couldn't copy the app's log: {ex.Message}"); }
}

[DllImport("user32.dll")] static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
[DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
[DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

[StructLayout(LayoutKind.Sequential)]
struct RECT { public int Left, Top, Right, Bottom; }
