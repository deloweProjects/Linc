using System.Diagnostics;
using Linc.Desktop.Services;

// Verification harness for M7a: opening one phone app in its own PC window (D-059).
// Runs entirely offline. The command line is built by the very method the app calls, and the
// one-window-per-package bookkeeping is driven through the real service with a stand-in
// launcher — no scrcpy is ever started and no phone is needed.
//
// M7b adds the per-app window store, so this harness DOES have a store now. It is constructed
// against a fresh temp directory every run and deleted afterwards (D-057), and section 9 proves
// the owner's real %LOCALAPPDATA%\Linc was never touched rather than merely intending it.
//
// The one thing it cannot do from a console is realize a tile: that needs a WinUI layout pass,
// and lives in tools\appgridprobe. What is checked here is the shape the shipped XAML must keep.
//
//   dotnet run --project tools/applaunchsim

Console.WriteLine("=== Linc App Window Verification Harness (applaunchsim) ===");

var failures = new List<string>();

void Check(bool ok, string passText, string failText, string failure)
{
    if (ok)
    {
        Console.WriteLine($"    PASS: {passText}");
    }
    else
    {
        Console.WriteLine($"    FAIL: {failText}");
        failures.Add(failure);
    }
}

const string Serial = "1B141FDEE0031X";
const string Package = "com.google.android.GoogleCamera";
const string Label = "Camera";

// A Pixel 7: 1080x2400 at 420dpi, the shape every geometry expectation below is derived from.
var pixel7 = (Width: 1080, Height: 2400, Dpi: 420);

// ---------------------------------------------------------------------------------------
// 1. The command line, argument by argument.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[1/13] Building the scrcpy command line for one app window...");
List<string> cmd = [];
try
{
    cmd = [.. AppLaunchService.BuildScrcpyArgs(Serial, Package, Label, pixel7)];
    Console.WriteLine($"    scrcpy {string.Join(" ", cmd.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");

    var serialAt = cmd.IndexOf("-s");
    Check(serialAt >= 0 && serialAt + 1 < cmd.Count && cmd[serialAt + 1] == Serial,
        "the device serial is passed with -s, so the window binds to THIS phone.",
        "no '-s <serial>' in the command line — with two phones attached scrcpy would pick one at random.",
        "serial not passed");

    // The heart of D-059: scrcpy starts the app itself. The '+' force-stops it first; the '?'
    // fuzzy-name form is never used, because v16's apps.get gives us the exact package and
    // guessing could only ever open the wrong app.
    Check(cmd.Contains($"--start-app=+{Package}"),
        $"--start-app=+{Package} — exact package, force-stop prefix.",
        $"the command line does not contain --start-app=+{Package}.",
        "--start-app=+<package> missing");
    Check(!cmd.Any(a => a.StartsWith("--start-app=?", StringComparison.Ordinal)),
        "the fuzzy '?' name form is not used anywhere.",
        "a --start-app=? fuzzy match is being used; we always know the exact package.",
        "fuzzy --start-app=? used");
    Check(!cmd.Contains($"--start-app={Package}"),
        "the package is never passed bare — the force-stop '+' is mandatory.",
        "--start-app=<package> is passed WITHOUT the leading '+'; a stale instance would be reused.",
        "--start-app missing the '+' prefix");

    Check(cmd.Any(a => a.StartsWith("--new-display=", StringComparison.Ordinal)),
        "a per-app virtual display is requested with --new-display=WxH/DPI.",
        "no --new-display — the app would take over the phone's own screen.",
        "--new-display missing");

    var titleAt = cmd.IndexOf("--window-title");
    Check(titleAt >= 0 && titleAt + 1 < cmd.Count && cmd[titleAt + 1] == Label,
        $"the window title is the app's label (\"{Label}\"), not its package id.",
        "the window title is not the app's label; the taskbar would read like a package manifest.",
        "window title is not the app label");
    Check(!cmd.Contains(Package) || cmd.IndexOf(Package) != titleAt + 1,
        "the package id is never used as the window caption.",
        "the package id is being used as the window caption.",
        "package used as window title");

    // M9f (A2): the owner's "they open in desktop mode, weird" complaint — a StatusBar on every
    // virtual display, because system decorations are on by default (BRAIN.md M9e). One flag turns
    // it off for per-app windows. See cli.c:708 for proof the flag exists in THIS vendored build.
    Check(cmd.Contains("--no-vd-system-decorations"),
        "--no-vd-system-decorations is passed — app windows no longer show a phone status bar.",
        "--no-vd-system-decorations is missing — app windows would still look like Desktop Mode.",
        "--no-vd-system-decorations missing from app-window args");

    // A2.2's boundary: Desktop Mode IS supposed to look like a desktop (D-053) and must NOT carry
    // the flag — through the REAL DesktopLaunchService, so a future "unify the two paths" change
    // fails here rather than only in a code review.
    var desktopCmd = DesktopLaunchService.BuildScrcpyArgs(Serial, new DesktopModeSettings());
    Console.WriteLine($"    Desktop Mode: scrcpy {string.Join(" ", desktopCmd.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");
    Check(!desktopCmd.Contains("--no-vd-system-decorations"),
        "Desktop Mode's own launch path does NOT carry the flag — it is supposed to look like a desktop.",
        "Desktop Mode's launch path now carries --no-vd-system-decorations; A2.2 forbids unifying the two paths.",
        "Desktop Mode wrongly carries --no-vd-system-decorations");

    // M11 B2.2: app windows never take the phone's audio — several can be open at once, and the
    // comment at AppLaunchService.cs:223 explains why this stays unconditional, unlike Desktop
    // Mode's ForwardAudio. Through the REAL pure static, so a future session that makes this
    // configurable fails here.
    Check(cmd.Contains("--no-audio"),
        "--no-audio is passed unconditionally for app windows — several can be open at once.",
        "--no-audio is missing from an app-window command line — app windows would fight over the audio stream.",
        "--no-audio missing from app-window args (B2.2)");

    // B2.2 also forbids making it configurable at all. Reflect on the real method signature so a
    // future session that adds an "AudioEnabled"/"ForwardAudio"-shaped parameter (even one that
    // still defaults to emitting --no-audio today) fails this loudly rather than only in review.
    var buildArgsMethod = typeof(AppLaunchService).GetMethod(nameof(AppLaunchService.BuildScrcpyArgs))!;
    var audioParam = buildArgsMethod.GetParameters()
        .FirstOrDefault(p => p.Name!.Contains("audio", StringComparison.OrdinalIgnoreCase));
    Check(audioParam is null,
        "AppLaunchService.BuildScrcpyArgs takes no audio-related parameter — app-window audio is not configurable.",
        $"AppLaunchService.BuildScrcpyArgs now has a parameter named '{audioParam?.Name}' — B2.2 forbids making app-window audio configurable.",
        "AppLaunchService.BuildScrcpyArgs gained an audio-configurable parameter (B2.2)");
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: building the command line threw: {ex.Message}");
    failures.Add($"BuildScrcpyArgs threw: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 2. The launch path contains NO ADB shell work at all (D-059/D-001).
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[2/13] The launch path must contain no 'am start' and no 'adb shell'...");
try
{
    var line = string.Join(" ", cmd);
    Check(!line.Contains("am start", StringComparison.OrdinalIgnoreCase),
        "no 'am start' anywhere — scrcpy starts the app itself.",
        "the command line contains 'am start'; D-059 replaced that whole approach.",
        "'am start' in the launch path");
    Check(!line.Contains("adb", StringComparison.OrdinalIgnoreCase)
          && !line.Contains("shell", StringComparison.OrdinalIgnoreCase),
        "no 'adb' and no 'shell' anywhere — nothing is executed on the phone by us.",
        "the command line shells out to the phone; the launch path must be pure scrcpy.",
        "adb/shell in the launch path");

    // Same check against the source, so a future session cannot reintroduce it below the
    // argument list either. (Crude on purpose — it fails loudly, which is the point.)
    var repoRoot = Directory.GetCurrentDirectory();
    while (!Directory.Exists(Path.Combine(repoRoot, "DESKTOP")) && Directory.GetParent(repoRoot) != null)
    {
        repoRoot = Directory.GetParent(repoRoot)!.FullName;
    }
    var servicePath = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "Services", "AppLaunchService.cs");
    var service = File.Exists(servicePath) ? File.ReadAllText(servicePath) : "";
    Check(service.Length > 0,
        "AppLaunchService.cs was found for the source-level checks.",
        "AppLaunchService.cs not found — the source-level checks could not run.",
        "AppLaunchService.cs not found");
    // Comment lines are stripped first: this file EXPLAINS that it does no `am start`, and the
    // check is about the code, not the prose.
    var serviceCode = string.Join("\n", service
        .Split('\n')
        .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    Check(!serviceCode.Contains("am start", StringComparison.OrdinalIgnoreCase)
          && !serviceCode.Contains("ExecuteRemoteCommand", StringComparison.Ordinal)
          && !serviceCode.Contains("AdbClient", StringComparison.Ordinal),
        "AppLaunchService itself holds no ADB client and issues no shell command.",
        "AppLaunchService reaches for ADB; D-059's whole point is that it does not have to.",
        "AppLaunchService uses ADB");
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: the no-ADB check threw: {ex.Message}");
    failures.Add($"no-ADB check threw: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 3. Geometry comes from the ONE shared routine, in MatchPhone mode.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[3/13] Geometry must be ComputeRecommendedGeometry's MatchPhone output...");
try
{
    var (w, h, dpi) = DesktopModeSettings.ComputeRecommendedGeometry(
        new DesktopModeSettings(AspectRatioMode: AspectRatioMode.MatchPhone),
        pixel7.Width, pixel7.Height, pixel7.Dpi);
    Console.WriteLine($"    shared routine, MatchPhone, Pixel 7 (1080x2400/420) -> {w}x{h}/{dpi}");

    Check(cmd.Contains($"--new-display={w}x{h}/{dpi}"),
        $"the window asks for --new-display={w}x{h}/{dpi} — the same numbers Desktop Mode would compute.",
        "the app window's geometry does not match the shared routine; there is a second geometry routine somewhere.",
        "geometry does not match ComputeRecommendedGeometry");

    // The properties that routine promises, restated here so a change to it that breaks
    // phone-shaped windows fails in THIS harness too.
    Check((long)w * h <= 1600L * 900L,
        "the window stays inside the 1,440,000-pixel budget.",
        "the window exceeds the pixel budget.", "pixel budget exceeded");
    Check(w % 2 == 0 && h % 2 == 0,
        "both dimensions are even (video encoders dislike odd ones).",
        "an odd dimension slipped through.", "odd dimension");
    Check(h > w,
        "the window is PORTRAIT — it looks like a phone, which is the point of MatchPhone.",
        "the window is not portrait for a portrait phone; the aspect ratio was not preserved.",
        "app window is not phone-shaped");
    var phoneAspect = (double)pixel7.Height / pixel7.Width;
    Check(Math.Abs((double)h / w - phoneAspect) < 0.02,
        "its aspect ratio matches the phone's to within 2%.",
        "its aspect ratio does not match the phone's.", "aspect ratio not preserved");
    Check(dpi is >= 120 and <= 320,
        $"the DPI ({dpi}) is inside the 120–320 band.",
        "the DPI is outside 120–320.", "DPI out of band");

    // Unreadable phone metrics must still open a phone-shaped window rather than refuse.
    var fallback = AppLaunchService.BuildScrcpyArgs(Serial, Package, Label, null);
    var (fw, fh, fdpi) = DesktopModeSettings.ComputeRecommendedGeometry(
        new DesktopModeSettings(AspectRatioMode: AspectRatioMode.MatchPhone),
        AppLaunchService.FallbackPhoneMetrics.Width,
        AppLaunchService.FallbackPhoneMetrics.Height,
        AppLaunchService.FallbackPhoneMetrics.Dpi);
    Check(fallback.Contains($"--new-display={fw}x{fh}/{fdpi}") && fh > fw,
        $"with the phone's metrics unreadable the window still opens phone-shaped ({fw}x{fh}/{fdpi}).",
        "the fallback geometry is missing or not phone-shaped.",
        "fallback geometry wrong");
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: the geometry check threw: {ex.Message}");
    failures.Add($"geometry check threw: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 4. One window per package: a second launch must not build a second command line.
//    Driven through the REAL service with a stand-in launcher — the counter below is the
//    number of processes it was actually asked to start.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[4/13] A second launch of the same package must not start a second window...");
var spawned = new List<IReadOnlyList<string>>();
try
{
    var log = new SilentLog();
    // The stand-in: records the command line it was handed and returns a harmless, long-lived
    // process that stands in for a scrcpy window until the harness closes it.
    Process Spawn(ProcessStartInfo info)
    {
        spawned.Add([.. info.ArgumentList]);
        var stand_in = new Process
        {
            StartInfo = new ProcessStartInfo("cmd.exe", "/c pause")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
            },
            EnableRaisingEvents = true
        };
        stand_in.Start();
        return stand_in;
    }

    using var service = new AppLaunchService(log, Spawn);

    await service.LaunchAsync(Serial, Package, Label, pixel7);
    var afterFirst = spawned.Count;
    await service.LaunchAsync(Serial, Package, Label, pixel7);
    var afterSecond = spawned.Count;

    Check(afterFirst == 1 && afterSecond == 1,
        "the second launch started nothing — the existing window is focused instead (2.2).",
        $"the second launch started another window ({afterSecond} processes for one package).",
        "a duplicate window was started for one package");
    Check(service.IsOpen(Package) && service.OpenPackages.Count == 1,
        "exactly one window is tracked for the package.",
        "the service does not track exactly one window for the package.",
        "window bookkeeping wrong");

    // A different app gets its own window: the dictionary is keyed by package, not "one window".
    await service.LaunchAsync(Serial, "org.mozilla.firefox", "Firefox", pixel7);
    Check(spawned.Count == 2 && service.OpenPackages.Count == 2,
        "a second, different app opens its own window alongside the first.",
        "launching a different app did not open its own window.",
        "second app did not get its own window");
    Check(spawned[1].Contains("--start-app=+org.mozilla.firefox"),
        "each window's command line names its own package.",
        "the second window's command line does not name its own package.",
        "second window has the wrong package");

    // 2.7: closing every window (what a disconnect does) must drop every entry, so the apps
    // can be opened again afterwards.
    await service.CloseAllAsync();
    var waited = 0;
    while (service.OpenPackages.Count > 0 && waited < 5000)
    {
        await Task.Delay(50);
        waited += 50;
    }
    Check(service.OpenPackages.Count == 0,
        "CloseAllAsync closed every window and dropped every entry (the disconnect path).",
        $"windows survived CloseAllAsync ({service.OpenPackages.Count} still tracked) — relaunching would be blocked.",
        "CloseAllAsync left orphan entries");

    await service.LaunchAsync(Serial, Package, Label, pixel7);
    Check(spawned.Count == 3,
        "after the windows closed, the same app opens again.",
        "relaunching after a close did nothing — a stale entry is still blocking it.",
        "relaunch after close blocked");
    await service.CloseAllAsync();
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: the one-window-per-package check threw: {ex.Message}");
    failures.Add($"one-window check threw: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 5. What the service actually handed the launcher is what the pure builder produces.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[5/13] The spawned command line must be the built one, verbatim...");
try
{
    Check(spawned.Count > 0 && spawned[0].SequenceEqual(
            AppLaunchService.BuildScrcpyArgs(Serial, Package, Label, pixel7)),
        "the process was started with exactly the arguments BuildScrcpyArgs returns.",
        "the spawned arguments differ from the built ones — this harness would be testing a fiction.",
        "spawned args differ from built args");
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: the spawn-args check threw: {ex.Message}");
    failures.Add($"spawn-args check threw: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 6. UI wiring. Crude string checks on purpose: they exist so a future session that unwires
//    the tile fails loudly here rather than shipping a grid that looks clickable and is not.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[6/13] UI wiring: the Apps tiles are interactive and bound to the launch command...");
try
{
    var repoRoot = Directory.GetCurrentDirectory();
    while (!Directory.Exists(Path.Combine(repoRoot, "DESKTOP")) && Directory.GetParent(repoRoot) != null)
    {
        repoRoot = Directory.GetParent(repoRoot)!.FullName;
    }
    var xamlPath = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "Views", "HomePage.xaml");
    var vmPath = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "ViewModels", "HomeViewModel.cs");
    var xaml = File.Exists(xamlPath) ? File.ReadAllText(xamlPath) : "";
    var vm = File.Exists(vmPath) ? File.ReadAllText(vmPath) : "";

    if (xaml.Length == 0 || vm.Length == 0)
    {
        Console.WriteLine("    FAIL: HomePage.xaml or HomeViewModel.cs not found.");
        failures.Add("Missing HomePage.xaml or HomeViewModel.cs");
    }
    else
    {
        var cardStart = xaml.IndexOf("<!-- BEGIN Apps section", StringComparison.Ordinal);
        var cardEnd = xaml.IndexOf("<!-- END Apps section -->", StringComparison.Ordinal);
        if (cardStart < 0 || cardEnd <= cardStart)
        {
            Console.WriteLine("    FAIL: could not locate the Apps section block in HomePage.xaml.");
            failures.Add("Apps section block not found in HomePage.xaml");
        }
        else
        {
            var card = xaml[cardStart..cardEnd];

            // M7a inverts M6c's deliberate inertness: the tiles now go somewhere.
            Check(!card.Contains("IsHitTestVisible=\"False\"", StringComparison.Ordinal),
                "the app grid is no longer hit-test invisible — the tiles can be clicked.",
                "the app grid is still IsHitTestVisible=\"False\"; every click would be swallowed.",
                "Apps grid still IsHitTestVisible=False");
            Check(card.Contains("Command=\"{x:Bind Launch}\"", StringComparison.Ordinal),
                "each tile binds its click to the launch command.",
                "no tile binds Command=\"{x:Bind Launch}\" — clicking one would do nothing.",
                "tile does not bind the launch command");
            Check(card.Contains("CommandParameter=\"{x:Bind}\"", StringComparison.Ordinal),
                "the tile passes itself as the command parameter, so the command knows which app.",
                "the tile passes no command parameter; the command could not tell which app was clicked.",
                "tile passes no command parameter");
            Check(card.Contains("<Button", StringComparison.Ordinal),
                "the click target is a Button (no selection semantics — a click opens, it does not select).",
                "there is no Button in the Apps tiles.",
                "no click target in the Apps tiles");
            Check(card.Contains("ButtonBackgroundPointerOver", StringComparison.Ordinal),
                "the tile has a hover affordance, so it reads as clickable.",
                "the tile has no hover affordance; a clickable tile that looks inert is the M6c complaint in reverse.",
                "no hover affordance on the Apps tiles");

            // M6c-2/M6c-3 structure that M7a must not disturb.
            Check(card.Contains("<ItemsRepeater", StringComparison.Ordinal)
                  && card.Contains("<UniformGridLayout", StringComparison.Ordinal)
                  && card.Contains("<ScrollViewer", StringComparison.Ordinal),
                "the icon grid is still ItemsRepeater + UniformGridLayout, still scrolling inside itself.",
                "the Apps section's grid or its internal ScrollViewer was restructured.",
                "Apps section structure changed");
            Check(!card.Contains("x:Bind Package", StringComparison.Ordinal),
                "the tiles are still icon + name only — no package id.",
                "a package id crept back onto the tiles.",
                "package id shown on the tiles");
            Check(card.Contains("ViewModel.AppsMessage", StringComparison.Ordinal),
                "the Apps section shows the launch status/error line.",
                "there is nowhere for a launch failure to appear; it would fail silently.",
                "no AppsMessage line in the Apps section");
        }

        // View model: the command, its guards, and the disconnect teardown.
        Check(vm.Contains("LaunchAppCommand", StringComparison.Ordinal),
            "HomeViewModel hands the launch command to every AppVm it builds.",
            "AppVm instances are built without the launch command; the tiles would bind to null.",
            "AppVm built without the launch command");
        Check(vm.Contains("private async Task LaunchAppAsync(AppVm? app)", StringComparison.Ordinal),
            "the launch command lives on HomeViewModel.",
            "no LaunchAppAsync command on HomeViewModel.",
            "no launch command");
        // M15b D2 FLIPPED THE PINNED STRING. The guard is unchanged and this check is exactly as
        // strong; only the sentence moved. It used to read "Connect your phone to open its apps
        // on this PC." — a statement of the link's state, rendered on Home at the same time as the
        // Phone card's caption. Home now states the connection once, so this one is an
        // instruction about the app instead. The check still pins an exact production string.
        Check(vm.Contains("Open this app again once your phone is linked.", StringComparison.Ordinal),
            "launching while disconnected is guarded and explained.",
            "there is no disconnected guard on the launch path.",
            "no disconnected guard");
        Check(vm.Contains("CloseAllAsync", StringComparison.Ordinal),
            "a disconnect closes every app window (2.7).",
            "nothing closes the app windows on disconnect — they would be left pointed at a dead phone.",
            "no disconnect teardown");
        Check(vm.Contains("_launchesInFlight", StringComparison.Ordinal),
            "a second click while a launch is in flight is swallowed (the echo class of bug).",
            "there is no in-flight guard; a double click could open two windows.",
            "no in-flight guard on launch");
        Check(vm.Contains("OnPropertyChanged(nameof(HasAppsMessage))", StringComparison.Ordinal),
            "the composed HasAppsMessage is re-raised where its source changes (M6a's trap).",
            "HasAppsMessage is never re-raised; the message line would not appear.",
            "HasAppsMessage not raised");

        Console.WriteLine("    (These crude checks exist so a future session that unwires the tiles fails loudly.)");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: UI wiring check threw: {ex.Message}");
    failures.Add($"UI wiring check threw: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 7. Remembered window geometry: the four flags are ABSENT when nothing is remembered and
//    PRESENT and correct when something is (M7b 1.3).
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[7/13] Window geometry flags: absent when unremembered, exact when remembered...");
string[] geometryFlags = ["--window-x", "--window-y", "--window-width", "--window-height"];
try
{
    var noMemory = AppLaunchService.BuildScrcpyArgs(Serial, Package, Label, pixel7);
    Check(!noMemory.Any(a => geometryFlags.Any(f => a.StartsWith(f, StringComparison.Ordinal))),
        "with nothing remembered, NONE of --window-x/y/width/height is passed — scrcpy places the window.",
        "a window flag is passed with nothing remembered; that is an invented default, which 1.3 forbids.",
        "window geometry flags emitted with no remembered geometry");

    var remembered = new AppWindowGeometry(320, 180, 480, 1000);
    var withMemory = AppLaunchService.BuildScrcpyArgs(Serial, Package, Label, pixel7, remembered);
    Console.WriteLine($"    remembered {remembered.Width}x{remembered.Height} at {remembered.X},{remembered.Y} -> " +
        string.Join(" ", withMemory.Where(a => geometryFlags.Any(f => a.StartsWith(f, StringComparison.Ordinal)))));
    Check(withMemory.Contains("--window-x=320") && withMemory.Contains("--window-y=180")
          && withMemory.Contains("--window-width=480") && withMemory.Contains("--window-height=1000"),
        "all four flags are passed, with the remembered numbers verbatim.",
        "the remembered rectangle is not passed through to scrcpy correctly.",
        "remembered geometry not passed correctly");
    Check(withMemory.Count == noMemory.Count + 4,
        "remembering a window adds exactly four arguments and changes nothing else.",
        "the remembered case differs from the unremembered one by something other than the four flags.",
        "geometry changed more than the four flags");

    // Half a rectangle is worse than none: it would move the window somewhere nobody put it.
    var nonsense = new AppWindowGeometry(0, 0, 0, 0);
    var withNonsense = AppLaunchService.BuildScrcpyArgs(Serial, Package, Label, pixel7, nonsense);
    Check(!withNonsense.Any(a => geometryFlags.Any(f => a.StartsWith(f, StringComparison.Ordinal))),
        "an implausible rectangle (zero size) is ignored rather than passed on.",
        "a zero-size rectangle was passed to scrcpy.",
        "implausible geometry passed to scrcpy");
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: the geometry-flag check threw: {ex.Message}");
    failures.Add($"geometry-flag check threw: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 8. The off-screen rule. A remembered rectangle whose monitor is gone must be DISCARDED,
//    not restored — a window nobody can see reads as "it didn't open".
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[8/13] An off-screen remembered rectangle must be discarded...");
try
{
    // Two monitors, the second to the right of the first — then unplug the second.
    IReadOnlyList<(int X, int Y, int Width, int Height)> twoMonitors =
        [(0, 0, 1920, 1040), (1920, 0, 1920, 1040)];
    IReadOnlyList<(int X, int Y, int Width, int Height)> oneMonitor = [(0, 0, 1920, 1040)];

    var onSecond = new AppWindowGeometry(2200, 100, 480, 1000);
    Check(AppWindowStore.IsOnScreen(onSecond, twoMonitors),
        "a window on the second monitor is on-screen while that monitor is plugged in.",
        "a perfectly visible window was judged off-screen.", "visible window judged off-screen");
    Check(!AppWindowStore.IsOnScreen(onSecond, oneMonitor),
        "the same window is discarded once that monitor is gone — it would open where nobody can see it.",
        "a window on an unplugged monitor was judged on-screen; it would open invisibly.",
        "off-screen window not discarded");

    // The rule is about the TITLE BAR, not any overlap: a window hanging off the bottom can be
    // grabbed, one hanging off the top cannot.
    var mostlyBelow = new AppWindowGeometry(100, 1000, 480, 1000);
    Check(AppWindowStore.IsOnScreen(mostlyBelow, oneMonitor),
        "a window hanging off the bottom is kept — its title bar is still grabbable.",
        "a window with a grabbable title bar was discarded.", "grabbable window discarded");
    var above = new AppWindowGeometry(100, -900, 480, 1000);
    Check(!AppWindowStore.IsOnScreen(above, oneMonitor),
        "a window whose title bar is above the screen is discarded — it could never be dragged back.",
        "a window with an unreachable title bar was restored.", "ungrabbable window restored");
    Check(!AppWindowStore.IsOnScreen(new AppWindowGeometry(100, 100, 480, 1000), []),
        "with no monitors readable at all, nothing is restored (the safe direction).",
        "a rectangle was restored with no monitor layout to check it against.",
        "geometry restored with no monitors");

    // And the same rule through the service, which is where it actually runs.
    var offScreenRoot = Path.Combine(Path.GetTempPath(), "linc-applaunchsim-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new AppWindowStore(offScreenRoot);
        store.Save(Serial, Package, onSecond);
        using var withTwo = new AppLaunchService(new SilentLog(), _ => throw new InvalidOperationException(),
            store, () => twoMonitors);
        using var withOne = new AppLaunchService(new SilentLog(), _ => throw new InvalidOperationException(),
            store, () => oneMonitor);
        Check(withTwo.RememberedGeometryFor(Serial, Package) == onSecond,
            "the service restores the remembered rectangle while its monitor is present.",
            "the service did not restore a valid remembered rectangle.",
            "service did not restore valid geometry");
        Check(withOne.RememberedGeometryFor(Serial, Package) is null,
            "the service returns nothing once that monitor is gone, so no window flags are emitted.",
            "the service handed back a rectangle on a monitor that is no longer there.",
            "service restored off-screen geometry");
    }
    finally
    {
        if (Directory.Exists(offScreenRoot)) Directory.Delete(offScreenRoot, recursive: true);
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: the off-screen check threw: {ex.Message}");
    failures.Add($"off-screen check threw: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 9. The store itself: round trip, corrupt file, and — the D-057/D-058 rule — the root comes
//    from DeviceRegistry, so a harness on a temp root CANNOT reach the owner's real cache.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[9/13] appwindows.json: round trip, corruption tolerance, and the injected root...");
var storeRoot = Path.Combine(Path.GetTempPath(), "linc-applaunchsim-" + Guid.NewGuid().ToString("N"));
try
{
    // The real store, recorded so this section can prove it never went near it.
    var realRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Linc");
    var realFile = Path.Combine(realRoot, "cache", Serial, "appwindows.json");
    var realExistedBefore = File.Exists(realFile);
    var realStampBefore = realExistedBefore ? File.GetLastWriteTimeUtc(realFile) : DateTime.MinValue;

    var registry = new DeviceRegistry(storeRoot);
    var store = new AppWindowStore(registry);

    var path = store.PathFor(Serial);
    Console.WriteLine($"    store path: {path}");
    Check(Path.GetFullPath(path).StartsWith(Path.GetFullPath(storeRoot), StringComparison.OrdinalIgnoreCase),
        "the store resolves under the INJECTED root, exactly as AppCatalog does (D-057/D-058).",
        $"the store resolved to {path}, outside the injected root — it is reaching for the real store.",
        "AppWindowStore does not honour the injected root");
    Check(!Path.GetFullPath(path).StartsWith(Path.GetFullPath(realRoot), StringComparison.OrdinalIgnoreCase),
        "it is nowhere near the owner's real %LOCALAPPDATA%\\Linc.",
        "the store resolved INSIDE the owner's real store; this harness would be writing their data.",
        "AppWindowStore resolved into the real store");
    Check(Path.GetDirectoryName(path) == Path.GetDirectoryName(new AppCatalog(registry).ListPathFor(Serial)),
        "it lives beside apps.json in the same per-device cache directory.",
        "appwindows.json is not beside apps.json; the per-device cache is now in two places.",
        "appwindows.json is not beside apps.json");

    // Round trip, several apps, and per-app isolation.
    var camera = new AppWindowGeometry(100, 200, 520, 1100);
    var firefox = new AppWindowGeometry(700, 50, 640, 1200);
    Check(store.Save(Serial, Package, camera) && store.Save(Serial, "org.mozilla.firefox", firefox),
        "two apps' rectangles were written.",
        "writing a rectangle failed.", "geometry write failed");
    Check(store.Get(Serial, Package) == camera && store.Get(Serial, "org.mozilla.firefox") == firefox,
        "both round-tripped through appwindows.json, each keyed by its own package.",
        "a rectangle did not round-trip.", "geometry round-trip failed");
    Check(store.Get(Serial, "com.example.never-opened") is null,
        "an app that has never been opened has no remembered rectangle (the first-launch case).",
        "an unopened app reported a remembered rectangle.", "unopened app has geometry");
    Check(store.Get("OTHER-SERIAL", Package) is null,
        "the same app on a different phone is a different window (per-device, per-app).",
        "one phone's remembered window leaked to another phone.", "geometry leaked across devices");

    // Writing a second app must not lose the first — the classic read-modify-write bug.
    store.Save(Serial, Package, camera with { X = 900 });
    Check(store.Get(Serial, "org.mozilla.firefox") == firefox,
        "updating one app's rectangle leaves the others intact.",
        "updating one app's rectangle dropped another's.", "geometry write clobbered other entries");

    // A rectangle we would never use is refused at the door rather than stored.
    Check(!store.Save(Serial, "com.example.zero", new AppWindowGeometry(10, 10, 0, 0))
          && store.Get(Serial, "com.example.zero") is null,
        "an implausible rectangle is refused — storing a wrong one is worse than storing nothing.",
        "an implausible rectangle was stored.", "implausible geometry stored");

    // Corruption: the file the product can regenerate for free must never take the app down.
    File.WriteAllText(path, "{ this is not json at all ");
    Check(store.Load(Serial).Count == 0 && store.Get(Serial, Package) is null,
        "a corrupt appwindows.json degrades to \"no memory\" instead of throwing.",
        "a corrupt appwindows.json did not degrade cleanly.", "corrupt appwindows.json not tolerated");
    Check(store.Save(Serial, Package, camera) && store.Get(Serial, Package) == camera,
        "and the next close simply rewrites it.",
        "the store could not recover from a corrupt file.", "no recovery from a corrupt file");

    // Well-formed JSON of the wrong shape, and a plausible-looking entry with junk values.
    File.WriteAllText(path, "[1, 2, 3]");
    Check(store.Load(Serial).Count == 0,
        "well-formed JSON of the wrong shape also degrades to \"no memory\".",
        "a JSON array where a map belongs was not tolerated.", "wrong-shaped JSON not tolerated");
    File.WriteAllText(path, "{\"com.example.tiny\":{\"X\":1,\"Y\":1,\"Width\":2,\"Height\":2}}");
    Check(store.Get(Serial, "com.example.tiny") is null,
        "an implausible rectangle in the FILE is dropped on read, not handed to scrcpy.",
        "a 2x2 window rectangle was read back and would have been used.",
        "implausible stored rectangle read back");

    // The D-057 proof proper: after everything above, the real store is exactly as we found it.
    Check(File.Exists(realFile) == realExistedBefore
          && (!realExistedBefore || File.GetLastWriteTimeUtc(realFile) == realStampBefore),
        "the owner's real %LOCALAPPDATA%\\Linc\\cache\\<serial>\\appwindows.json was never touched.",
        "this harness wrote into the owner's REAL store — the exact failure D-057 exists to prevent.",
        "the real store was touched");

    // Source-level: the store must never resolve its own root. This is the check that fails if a
    // future session swaps the injected root for Environment.GetFolderPath.
    var repoRoot = Directory.GetCurrentDirectory();
    while (!Directory.Exists(Path.Combine(repoRoot, "DESKTOP")) && Directory.GetParent(repoRoot) != null)
    {
        repoRoot = Directory.GetParent(repoRoot)!.FullName;
    }
    var storeSourcePath = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "Services", "AppWindowStore.cs");
    var storeSource = File.Exists(storeSourcePath) ? File.ReadAllText(storeSourcePath) : "";
    Check(storeSource.Length > 0,
        "AppWindowStore.cs was found for the source-level check.",
        "AppWindowStore.cs not found — the D-057 source check could not run.",
        "AppWindowStore.cs not found");
    var storeCode = string.Join("\n", storeSource
        .Split('\n')
        .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)
                    && !l.TrimStart().StartsWith("///", StringComparison.Ordinal)));
    Check(storeSource.Length > 0
          && !storeCode.Contains("GetFolderPath", StringComparison.Ordinal)
          && !storeCode.Contains("SpecialFolder", StringComparison.Ordinal),
        "AppWindowStore never calls Environment.GetFolderPath — its root is always handed to it.",
        "AppWindowStore resolves its own root via Environment.GetFolderPath; that reopens the hole D-057 closed.",
        "AppWindowStore resolves its own root");
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: the store check threw: {ex.Message}");
    failures.Add($"store check threw: {ex.Message}");
}
finally
{
    try
    {
        if (Directory.Exists(storeRoot)) Directory.Delete(storeRoot, recursive: true);
    }
    catch (IOException)
    {
        // A leftover temp directory is harmless; failing the run over it would not be.
    }
}

// ---------------------------------------------------------------------------------------
// 10. UI wiring for M7b: lazy icons driven by realized tiles, the grid's virtualizing shape,
//     and the open-window count + "Close all".
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[10/13] UI wiring: lazy icons, the virtualizing grid, and the open-window affordance...");
try
{
    var repoRoot = Directory.GetCurrentDirectory();
    while (!Directory.Exists(Path.Combine(repoRoot, "DESKTOP")) && Directory.GetParent(repoRoot) != null)
    {
        repoRoot = Directory.GetParent(repoRoot)!.FullName;
    }
    var desktop = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop");
    var xaml = File.ReadAllText(Path.Combine(desktop, "Views", "HomePage.xaml"));
    var codeBehind = File.ReadAllText(Path.Combine(desktop, "Views", "HomePage.xaml.cs"));
    var vm = File.ReadAllText(Path.Combine(desktop, "ViewModels", "HomeViewModel.cs"));

    var cardStart = xaml.IndexOf("<!-- BEGIN Apps section", StringComparison.Ordinal);
    var cardEnd = xaml.IndexOf("<!-- END Apps section -->", StringComparison.Ordinal);
    var card = cardStart >= 0 && cardEnd > cardStart ? xaml[cardStart..cardEnd] : "";
    Check(card.Length > 0,
        "the Apps section block was located in HomePage.xaml.",
        "could not locate the Apps section block in HomePage.xaml.",
        "Apps section block not found");

    // 1.1 — the structural half of virtualization. The repeater must be the ScrollViewer's DIRECT
    // content: nothing between the two tags. appgridprobe measures the realized count itself (and
    // found ~75 of 500); this check is what keeps the shipped nesting from drifting.
    var scrollerAt = card.IndexOf("<ScrollViewer", StringComparison.Ordinal);
    var repeaterAt = card.IndexOf("<ItemsRepeater", StringComparison.Ordinal);
    var between = scrollerAt >= 0 && repeaterAt > scrollerAt ? card[scrollerAt..repeaterAt] : "!";
    var openTags = System.Text.RegularExpressions.Regex.Matches(between, "<[A-Za-z]");
    Check(scrollerAt >= 0 && repeaterAt > scrollerAt && openTags.Count == 1,
        "the ItemsRepeater is the internal ScrollViewer's DIRECT content — no wrapper in between.",
        "something now sits between the Apps ScrollViewer and its ItemsRepeater; the scrolling host must drive the repeater directly.",
        "a wrapper element sits between the Apps ScrollViewer and its ItemsRepeater");

    // 1.2 — icons are fetched per realized tile, and NOT swept over the whole list.
    Check(card.Contains("ElementPrepared=\"OnAppTilePrepared\"", StringComparison.Ordinal)
          && card.Contains("ElementClearing=\"OnAppTileClearing\"", StringComparison.Ordinal),
        "the repeater raises ElementPrepared/ElementClearing into the page — the lazy-icon trigger.",
        "the Apps repeater has no realized-tile handlers; icon fetching could not be lazy.",
        "no ElementPrepared/ElementClearing on the Apps repeater");
    Check(codeBehind.Contains("OnAppTileRealized", StringComparison.Ordinal)
          && codeBehind.Contains("OnAppTileUnrealized", StringComparison.Ordinal),
        "the page forwards both to the view model.",
        "the page does not forward tile realization to the view model.",
        "tile realization not forwarded to the view model");
    Check(vm.Contains("public void OnAppTileRealized(AppVm? app)", StringComparison.Ordinal),
        "HomeViewModel fetches an icon when a tile is realized.",
        "HomeViewModel has no realized-tile entry point.", "no realized-tile entry point");
    Check(!vm.Contains("private void LoadAppIcons()", StringComparison.Ordinal)
          && !vm.Contains("LoadAppIcons();", StringComparison.Ordinal),
        "the M6c whole-list icon sweep is GONE — this is the fetch that made a 300-app phone cost 300 requests.",
        "the whole-list icon sweep is still there; 1.2's laziness is cosmetic while it survives.",
        "the whole-list icon sweep survives");

    // The three M6c guards must survive intact — scrolling must not turn a miss into a retry loop
    // or re-request an icon we already hold.
    Check(vm.Contains("_appIconMisses", StringComparison.Ordinal)
          && vm.Contains("_appIconMisses.Contains(app.Package)", StringComparison.Ordinal),
        "a package with no icon is still asked once, ever — the miss set guards the realized path too.",
        "the icon-miss guard is not applied on the realized-tile path; scrolling would re-ask forever.",
        "icon-miss guard missing on the realized path");
    Check(vm.Contains("_appIconsInFlight.Add(app.Package)", StringComparison.Ordinal),
        "an in-flight fetch is not started twice when a tile is realized again mid-scroll.",
        "the in-flight guard is missing on the realized-tile path.",
        "in-flight icon guard missing");
    Check(vm.Contains("app.Icon is not null", StringComparison.Ordinal),
        "a tile whose AppVm already holds a decoded icon asks for nothing — scrolling back is free.",
        "a realized tile re-fetches an icon it already has.", "cached icon re-fetched on realize");
    Check(vm.Contains("_appsRefreshInFlight", StringComparison.Ordinal),
        "the M6c refresh guard survives.", "the refresh guard was dropped.", "refresh guard dropped");

    // Section 2 — the open-window affordance, and nothing more than it.
    Check(vm.Contains("OpenAppWindowsText", StringComparison.Ordinal)
          && card.Contains("ViewModel.OpenAppWindowsText", StringComparison.Ordinal),
        "the Apps section shows how many app windows are open.",
        "there is no open-window count on the Apps section.", "no open-window count");
    Check(card.Contains("ViewModel.CloseAllAppWindowsCommand", StringComparison.Ordinal),
        "\"Close all\" is bound to the command that closes every window.",
        "there is no \"Close all\" action bound in the Apps section.", "no Close all action");
    Check(card.Contains("Visibility=\"{x:Bind ViewModel.HasOpenAppWindows, Mode=OneWay}\"", StringComparison.Ordinal),
        "both appear only while at least one window is open.",
        "the count/Close all are not gated on there being an open window.",
        "open-window affordance not gated");
    Check(vm.Contains("OnPropertyChanged(nameof(OpenAppWindowsText))", StringComparison.Ordinal),
        "the count is re-raised when windows open and close (M6a's composed-property trap).",
        "OpenAppWindowsText is never re-raised; the count would freeze.",
        "OpenAppWindowsText not re-raised");
    Check(!card.Contains("Thumbnail", StringComparison.Ordinal)
          && !card.Contains("WindowList", StringComparison.Ordinal),
        "it stayed modest — no per-window list, no thumbnails, no docking (2, decided).",
        "the Apps section grew a window manager; 2 explicitly rules that out.",
        "the open-window affordance overreached");

    // 1.3's wiring: the service is given a store, and the store's root comes from the registry.
    var app = File.ReadAllText(Path.Combine(desktop, "App.xaml.cs"));
    Check(app.Contains("new AppWindowStore(", StringComparison.Ordinal)
          && app.Contains("GetRequiredService<IDeviceRegistry>().RootPath", StringComparison.Ordinal),
        "the app builds the window store on DeviceRegistry.RootPath (D-057/D-058).",
        "the window store is not built on the registry's root; production would resolve its own path.",
        "window store not built on the registry root");
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: the M7b UI wiring check threw: {ex.Message}");
    failures.Add($"M7b UI wiring check threw: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 11. M9e (B3.2): the measured concurrent-window cap. B2's hypothesis was that the virtual-
//     display COUNT itself crashes windows; measured directly this session (8 concurrent
//     --new-display/--start-app windows on a real Pixel 7, staggered AND near-simultaneous) and
//     found NO crash at that count. What measurement did find — a virtual display leaking when
//     its owning process is force-killed, and real memory pressure under several concurrent full
//     Android apps — justifies a precautionary cap instead (AppLaunchService.MaxConcurrentWindows,
//     see its doc comment for the full reasoning). This proves the cap through the REAL service:
//     MaxConcurrentWindows windows open normally; the next DIFFERENT package is refused with a
//     plain-language ErrorRaised message and spawns nothing; OpenWindowCount does not exceed the
//     cap; and focusing an ALREADY-OPEN window is never blocked by it.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[11/13] The concurrent-window cap refuses a new package once full...");
try
{
    var spawnedCap = new List<IReadOnlyList<string>>();
    var errors = new List<string>();
    Process SpawnCap(ProcessStartInfo info)
    {
        spawnedCap.Add([.. info.ArgumentList]);
        var standIn = new Process
        {
            StartInfo = new ProcessStartInfo("cmd.exe", "/c pause")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
            },
            EnableRaisingEvents = true
        };
        standIn.Start();
        return standIn;
    }

    var capLog = new SilentLog();
    using var capService = new AppLaunchService(capLog, SpawnCap);
    capService.ErrorRaised += msg => errors.Add(msg);

    for (var n = 0; n < AppLaunchService.MaxConcurrentWindows; n++)
    {
        await capService.LaunchAsync(Serial, $"com.example.app{n}", $"App{n}", pixel7);
    }
    Check(spawnedCap.Count == AppLaunchService.MaxConcurrentWindows && capService.OpenWindowCount == AppLaunchService.MaxConcurrentWindows,
        $"exactly {AppLaunchService.MaxConcurrentWindows} windows opened, filling the cap.",
        $"expected {AppLaunchService.MaxConcurrentWindows} open windows, got {capService.OpenWindowCount} ({spawnedCap.Count} spawned).",
        "cap fill did not reach MaxConcurrentWindows");

    // One more DIFFERENT package must be refused, not spawned.
    errors.Clear();
    await capService.LaunchAsync(Serial, "com.example.oneTooMany", "OneTooMany", pixel7);
    Check(spawnedCap.Count == AppLaunchService.MaxConcurrentWindows,
        "the (cap+1)th DIFFERENT package spawned nothing.",
        $"the (cap+1)th launch spawned a process anyway ({spawnedCap.Count} total).",
        "cap did not block an extra window");
    Check(capService.OpenWindowCount == AppLaunchService.MaxConcurrentWindows,
        $"OpenWindowCount stayed at the cap ({AppLaunchService.MaxConcurrentWindows}), never exceeding it.",
        $"OpenWindowCount is {capService.OpenWindowCount}, expected it to stay at the cap.",
        "OpenWindowCount exceeded the cap");
    Check(errors.Count == 1 && !errors[0].Contains("adb", StringComparison.OrdinalIgnoreCase)
          && !errors[0].Contains("Exception", StringComparison.OrdinalIgnoreCase),
        $"a plain-language refusal was raised: \"{(errors.Count > 0 ? errors[0] : "")}\"",
        $"expected exactly one plain-language ErrorRaised message, got {errors.Count}.",
        "cap refusal message missing or not plain-language");

    // Focusing an ALREADY-OPEN window must never be blocked by the cap.
    errors.Clear();
    await capService.LaunchAsync(Serial, "com.example.app0", "App0", pixel7);
    Check(spawnedCap.Count == AppLaunchService.MaxConcurrentWindows && errors.Count == 0,
        "re-launching an already-open package at the cap re-focuses it, spawns nothing, and raises no error.",
        "re-launching an already-open package at the cap was wrongly treated as a new window.",
        "cap wrongly blocked focusing an existing window");

    await capService.CloseAllAsync();
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: the concurrent-window cap check threw: {ex.Message}");
    failures.Add($"Concurrent-window cap check threw: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 12. M9e (B3.3): one window's process dying must not take the others with it, and the open
//     count must not drift. Measured directly against real scrcpy.exe processes this session
//     (kill one of two concurrently-open app windows; the other stayed alive) — this proves the
//     SAME independence through the real service's own process-exit handling, using the harness's
//     stand-in processes so no scrcpy or phone is needed.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[12/13] One window's process dying leaves the others open, count intact...");
try
{
    var standIns = new List<Process>();
    Process SpawnIndependent(ProcessStartInfo info)
    {
        var standIn = new Process
        {
            StartInfo = new ProcessStartInfo("cmd.exe", "/c pause")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
            },
            EnableRaisingEvents = true
        };
        standIn.Start();
        standIns.Add(standIn);
        return standIn;
    }

    var indepLog = new SilentLog();
    using var indepService = new AppLaunchService(indepLog, SpawnIndependent);
    var indepErrors = new List<string>();
    indepService.ErrorRaised += msg => indepErrors.Add(msg);

    await indepService.LaunchAsync(Serial, "com.example.survivorA", "SurvivorA", pixel7);
    await indepService.LaunchAsync(Serial, "com.example.survivorB", "SurvivorB", pixel7);
    await indepService.LaunchAsync(Serial, "com.example.victim", "Victim", pixel7);
    Check(indepService.OpenWindowCount == 3,
        "all three windows are tracked before the kill.",
        $"expected 3 open windows before the kill, got {indepService.OpenWindowCount}.",
        "pre-kill window count wrong");

    // Kill the middle process the way a real crash would: no CloseMainWindow, no CloseAllAsync —
    // an abrupt external death, exactly what B3.3 says must not cascade.
    standIns[2].Kill(entireProcessTree: true);
    var waitedIndep = 0;
    while (indepService.OpenWindowCount == 3 && waitedIndep < 5000)
    {
        await Task.Delay(50);
        waitedIndep += 50;
    }

    Check(indepService.OpenWindowCount == 2,
        "exactly the killed window's entry was dropped; the count is now 2, not 0 or 3.",
        $"expected 2 open windows after the kill, got {indepService.OpenWindowCount} — a crash is cascading or not being noticed.",
        "window count did not drop by exactly one after a crash");
    Check(indepService.IsOpen("com.example.survivorA") && indepService.IsOpen("com.example.survivorB"),
        "both OTHER windows are still tracked as open — the crash did not take them down.",
        "a surviving window's entry was wrongly dropped when a SIBLING process died.",
        "a sibling window was wrongly dropped");
    Check(!indepService.IsOpen("com.example.victim"),
        "the killed window's own entry is gone.",
        "the killed window is still tracked as open.",
        "killed window still tracked");
    Check(indepErrors.Any(e => e.Contains("closed unexpectedly", StringComparison.OrdinalIgnoreCase)),
        "the unexpected death raised a plain-language error naming what happened.",
        "no plain-language error was raised for the unexpected death.",
        "no error raised for unexpected window death");

    await indepService.CloseAllAsync();
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: the process-independence check threw: {ex.Message}");
    failures.Add($"Process-independence check threw: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 13. M9f (B2.1/B3.2): closing an app window is graceful-first, kill-as-fallback. A forced kill
//     skips scrcpy's own virtual-display teardown — M9e measured orphaned displays surviving a
//     killed process. Proves: (a) the pure escalation decision at both ends of its boundary, (b)
//     through the REAL service, a process with a real window that answers WM_CLOSE is NEVER
//     killed, (c) through the REAL service, a process with nothing to gracefully close DOES fall
//     back to Kill and logs it (never-silent, BRAIN.md), and (d) a crude source-text check that
//     CloseMainWindow is reached before Kill inside CloseAllAsync — crude on purpose (BRAIN.md
//     4.1): (b)/(c) already prove the real behaviour, this exists only so a future reordering of
//     the two calls fails loudly here, and is the mechanism for this part's negative proof.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[13/13] Graceful close before kill...");
try
{
    // (a) the pure decision, at both ends.
    Check(AppLaunchService.ShouldKillAfterGracefulClose(exitedWithinTimeout: false),
        "a process that did NOT exit within the timeout is marked for killing.",
        "a process that timed out was not marked for killing.",
        "ShouldKillAfterGracefulClose(false) wrong");
    Check(!AppLaunchService.ShouldKillAfterGracefulClose(exitedWithinTimeout: true),
        "a process that DID exit within the timeout is never killed.",
        "a process that exited gracefully was still marked for killing.",
        "ShouldKillAfterGracefulClose(true) wrong");

    // (b) through the real service: a REAL top-level GUI window (Character Map — a genuine classic
    // win32 process that owns its own HWND directly. Measured first: modern Windows' own notepad.exe
    // does NOT work for this — Start-Process returns a live, non-exited process whose MainWindowHandle
    // never resolves, because the actual window belongs to a SEPARATE process entirely (verified via
    // Get-Process: two distinct PIDs, one exited-looking stub and one real window owner). charmap.exe
    // has no such indirection: MainWindowHandle resolves immediately and CloseMainWindow ends it.)
    // responds to CloseMainWindow's WM_CLOSE and exits on its own — the Kill fallback must never
    // fire, and nothing is logged as a graceful-close timeout. This is the empirical B2.2 check:
    // does a real window actually die on WM_CLOSE the way scrcpy (an ordinary SDL/win32 app) is
    // expected to?
    var gracefulLog = new RecordingLog();
    Process? gracefulProc = null;
    using (var gracefulService = new AppLaunchService(gracefulLog, _ =>
    {
        var p = new Process
        {
            StartInfo = new ProcessStartInfo("charmap.exe") { UseShellExecute = false },
            EnableRaisingEvents = true
        };
        p.Start();
        gracefulProc = p;
        return p;
    }))
    {
        await gracefulService.LaunchAsync(Serial, "com.example.graceful", "Graceful", pixel7);
        var waitedForHwnd = 0;
        gracefulProc!.Refresh();
        while (gracefulProc.MainWindowHandle == IntPtr.Zero && waitedForHwnd < 3000)
        {
            await Task.Delay(50);
            waitedForHwnd += 50;
            gracefulProc.Refresh();
        }
        Console.WriteLine($"    [diag] graceful stand-in (Character Map) MainWindowHandle={gracefulProc.MainWindowHandle} after {waitedForHwnd}ms");
        var closeSw = System.Diagnostics.Stopwatch.StartNew();
        await gracefulService.CloseAllAsync();
        closeSw.Stop();
        Console.WriteLine($"    [diag] graceful close took {closeSw.ElapsedMilliseconds}ms; log messages: {string.Join(" | ", gracefulLog.Messages)}");
        Check(gracefulService.OpenPackages.Count == 0,
            "the real-windowed stand-in closed and its entry was dropped.",
            "the real-windowed stand-in is still tracked after CloseAllAsync.",
            "real-windowed stand-in not closed");
        Check(!gracefulLog.Messages.Any(m => m.Contains("did not close gracefully", StringComparison.Ordinal)),
            $"no 'did not close gracefully' warning was logged ({closeSw.ElapsedMilliseconds}ms) — the Kill fallback never fired.",
            "a graceful-close timeout was logged for a window that answers WM_CLOSE; Kill fired when it should not have.",
            "Kill fallback fired on a window that closed gracefully");
    }

    // (c) through the real service: a stand-in with NO window (the CreateNoWindow=true shape used
    // everywhere else in this harness) has nothing for CloseMainWindow to close, so the fallback
    // MUST fire, and it must say so in the log, naming the package. A FRESH ProcessStartInfo, like
    // every other section's stand-in — reusing the production `info` here would carry its already-
    // populated ArgumentList, which .NET refuses to combine with a plain Arguments string.
    var killLog = new RecordingLog();
    using (var killService = new AppLaunchService(killLog, _ =>
    {
        var p = new Process
        {
            StartInfo = new ProcessStartInfo("cmd.exe", "/c pause")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
            },
            EnableRaisingEvents = true
        };
        p.Start();
        return p;
    }))
    {
        await killService.LaunchAsync(Serial, "com.example.nowindow", "NoWindow", pixel7);
        await killService.CloseAllAsync();
        Console.WriteLine($"    [diag] kill-path log messages: {string.Join(" | ", killLog.Messages)}");
        Check(killService.OpenPackages.Count == 0,
            "the no-window stand-in was still closed (via the Kill fallback) and its entry dropped.",
            "the no-window stand-in is still tracked after CloseAllAsync.",
            "no-window stand-in not closed");
        Check(killLog.Messages.Any(m => m.Contains("did not close gracefully", StringComparison.Ordinal)
                                         && m.Contains("com.example.nowindow", StringComparison.Ordinal)),
            "a window with nothing to gracefully close falls back to Kill AND logs it, naming the package.",
            "the Kill fallback did not log — a future silent kill would be invisible (BRAIN.md's silent-catch rule).",
            "Kill fallback not logged");
    }

    // (d) crude source-text check (BRAIN.md 4.1): CloseAllAsync must reach CloseMainWindow before
    // it can reach Kill. Deliberately crude — string order in the method body, nothing more. This
    // is what a broken production file (Kill called first) fails against for the negative proof.
    var repoRoot13 = Directory.GetCurrentDirectory();
    while (!Directory.Exists(Path.Combine(repoRoot13, "DESKTOP")) && Directory.GetParent(repoRoot13) != null)
    {
        repoRoot13 = Directory.GetParent(repoRoot13)!.FullName;
    }
    var servicePath13 = Path.Combine(repoRoot13, "DESKTOP", "Linc.Desktop", "Services", "AppLaunchService.cs");
    var serviceSource13 = File.Exists(servicePath13) ? File.ReadAllText(servicePath13) : "";
    var closeAllAt = serviceSource13.IndexOf("public Task CloseAllAsync()", StringComparison.Ordinal);
    var closeAllBody = closeAllAt >= 0
        ? serviceSource13[closeAllAt..Math.Min(serviceSource13.Length, closeAllAt + 2500)]
        : "";
    var closeMainAt = closeAllBody.IndexOf("CloseMainWindow()", StringComparison.Ordinal);
    var killAt = closeAllBody.IndexOf("Kill(entireProcessTree", StringComparison.Ordinal);
    Check(closeAllBody.Length > 0 && closeMainAt >= 0 && killAt > closeMainAt,
        "in the source, CloseMainWindow() textually precedes Kill(entireProcessTree...) inside CloseAllAsync.",
        "CloseAllAsync's source no longer reaches CloseMainWindow before Kill — the graceful-first order was lost.",
        "CloseMainWindow no longer precedes Kill in CloseAllAsync");
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: the graceful-close check threw: {ex.Message}");
    failures.Add($"graceful-close check threw: {ex.Message}");
}

Console.WriteLine("\n=== SUMMARY ===");
if (failures.Count == 0)
{
    Console.WriteLine("PASS: All verification checks succeeded.");
    return 0;
}
Console.WriteLine($"FAIL: {failures.Count} check(s) failed:");
foreach (var f in failures)
{
    Console.WriteLine($"  - {f}");
}
return 1;

/// <summary>A log that writes nowhere — this harness must not create %LOCALAPPDATA%\Linc/logs.</summary>
sealed class SilentLog : ILogService
{
    public IReadOnlyList<LogEntry> Entries => [];
    public event Action? EntriesChanged { add { } remove { } }
    public string LogFolderPath => "";
    public void Log(LogLevel level, string message) { }
    public void Clear() { }
}

/// <summary>M9f §13: like <see cref="SilentLog"/>, but keeps every message so the graceful-close
/// warning can be asserted on. Still writes nowhere on disk.</summary>
sealed class RecordingLog : ILogService
{
    public List<string> Messages { get; } = [];
    public IReadOnlyList<LogEntry> Entries => [];
    public event Action? EntriesChanged { add { } remove { } }
    public string LogFolderPath => "";
    public void Log(LogLevel level, string message) => Messages.Add(message);
    public void Clear() { }
}
