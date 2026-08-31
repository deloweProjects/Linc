using System.Diagnostics;
using Linc.Desktop.Services;

// M12f Part A / M12g-amend Part A0-A1 / M12g Parts A-B: "stop needing a second PC" — a
// clean-room harness on the dev machine.
//
//   dotnet run --project tools/cleanroomsim
//
// Three independent sections, kept clearly separated below because they prove different things:
//   GUARD (source-text) — scans DESKTOP\Linc.Desktop\**\*.cs for LocalApplicationData resolved
//     anywhere outside the 3 sanctioned sites (M12g-amend §A1.4). This is what keeps the D-057/
//     D-058 hole shut after this task closes it.
//   HALF ONE (deterministic) — calls the REAL ToolLocator lookup functions against a fixture
//     publish-shaped directory with the environment sanitised, and would have caught M12c.
//   HALF TWO (launch) — launches the REAL published exe with --data-root pointed at a temp root
//     (M12g Part B) and would have caught M12b's missing .pri. It only runs when the publish
//     output exists and no other Linc.Desktop is already running; otherwise it is an honest,
//     loud SKIP.
//
// D-057 applies throughout: every fixture/temp root here is disposable, restored/deleted in a
// finally. The whole run — not just Half Two — is bracketed by a before/after snapshot of the
// owner's REAL %LOCALAPPDATA%\Linc\ (settings.json, tls-identity.pfx, logs\, cache\) that fails
// the harness if any of it moved (M12g §3.7 / M12g-amend §A1.4).

Console.WriteLine("=== Linc Clean-Room Harness (cleanroomsim) ===");

var failures = new List<string>();
var skips = new List<string>();

void Pass(string msg) => Console.WriteLine($"    PASS: {msg}");
void Fail(string msg, string failure)
{
    Console.WriteLine($"    FAIL: {msg}");
    failures.Add(failure);
}
void Skip(string msg)
{
    Console.WriteLine($"    SKIP: {msg}");
    skips.Add(msg);
}

// ---- Real-store snapshot (M12g §3.7 / M12g-amend §A1.4) ----
// Taken before ANYTHING else runs and compared after everything below finishes, so a leak from
// any section — not just Half Two — gets caught.

var realRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Linc");
var realSettingsPath = Path.Combine(realRoot, "settings.json");
var realPfxPath = Path.Combine(realRoot, "tls-identity.pfx");
var realLogsDir = Path.Combine(realRoot, "logs");
var realCacheDir = Path.Combine(realRoot, "cache");

(long Size, DateTime WriteUtc)? SnapshotFile(string path) =>
    File.Exists(path) ? (new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)) : null;

// Per-file (size, mtime), not just a set of paths — a HashSet of paths alone would miss an
// EXISTING file (e.g. today's already-there log file) growing in place, which is exactly what
// LogService.AppendToFile does on every write. Caught this the hard way: negative proof #2
// (reverting the DI registration) silently grew the real logs\ file while a paths-only set still
// reported "no new file" — see the session report.
Dictionary<string, (long Size, DateTime WriteUtc)> SnapshotDir(string dir) =>
    Directory.Exists(dir)
        ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => (new FileInfo(f).Length, File.GetLastWriteTimeUtc(f)), StringComparer.OrdinalIgnoreCase)
        : new Dictionary<string, (long, DateTime)>(StringComparer.OrdinalIgnoreCase);

string Describe((long Size, DateTime WriteUtc)? snap) =>
    snap is { } s ? $"{s.Size} bytes, {s.WriteUtc:O}" : "(absent)";

var settingsBefore = SnapshotFile(realSettingsPath);
var pfxBefore = SnapshotFile(realPfxPath);
var logsBefore = SnapshotDir(realLogsDir);
var cacheBefore = SnapshotDir(realCacheDir);

Console.WriteLine($"\n[real store, BEFORE] {realSettingsPath}: {Describe(settingsBefore)}");
Console.WriteLine($"[real store, BEFORE] {realPfxPath}: {Describe(pfxBefore)}");
Console.WriteLine($"[real store, BEFORE] {realLogsDir}: {logsBefore.Count} file(s)");
Console.WriteLine($"[real store, BEFORE] {realCacheDir}: {cacheBefore.Count} file(s)");

Console.WriteLine("\n########## GUARD: LocalApplicationData resolved only at the 3 sanctioned sites ##########");
RunLocalAppDataGuard();

Console.WriteLine("\n########## HALF ONE: real ToolLocator lookups, environment sanitised ##########");
RunHalfOne();

Console.WriteLine("\n########## HALF TWO: does the published app survive with no help from this machine? ##########");
RunHalfTwo();

Console.WriteLine("\n########## GUARD: the owner's real store is untouched across the whole run ##########");
{
    var settingsAfter = SnapshotFile(realSettingsPath);
    var pfxAfter = SnapshotFile(realPfxPath);
    var logsAfter = SnapshotDir(realLogsDir);
    var cacheAfter = SnapshotDir(realCacheDir);

    Console.WriteLine($"    {realSettingsPath}: before={Describe(settingsBefore)} after={Describe(settingsAfter)}");
    if (settingsBefore == settingsAfter)
    {
        Pass("real settings.json size and last-write time unchanged across the whole run.");
    }
    else
    {
        Fail("real settings.json size or last-write time CHANGED across the run.", "settings.json was modified — the store sandbox did not hold");
    }

    Console.WriteLine($"    {realPfxPath}: before={Describe(pfxBefore)} after={Describe(pfxAfter)}");
    if (pfxBefore == pfxAfter)
    {
        Pass("real tls-identity.pfx size and last-write time unchanged across the whole run.");
    }
    else
    {
        Fail("real tls-identity.pfx size or last-write time CHANGED across the run.", "tls-identity.pfx was modified — the phone's pinned TLS identity is at risk");
    }

    List<string> ChangedFiles(Dictionary<string, (long Size, DateTime WriteUtc)> before, Dictionary<string, (long Size, DateTime WriteUtc)> after)
    {
        var changed = new List<string>();
        foreach (var (path, afterStat) in after)
        {
            if (!before.TryGetValue(path, out var beforeStat))
            {
                changed.Add($"{path} (NEW)");
            }
            else if (beforeStat != afterStat)
            {
                changed.Add($"{path} (MODIFIED: {beforeStat.Size}b/{beforeStat.WriteUtc:O} -> {afterStat.Size}b/{afterStat.WriteUtc:O})");
            }
        }
        foreach (var path in before.Keys.Except(after.Keys))
        {
            changed.Add($"{path} (DELETED)");
        }
        return changed;
    }

    var changedLogFiles = ChangedFiles(logsBefore, logsAfter);
    Console.WriteLine($"    {realLogsDir}: before={logsBefore.Count} file(s) after={logsAfter.Count} file(s)");
    if (changedLogFiles.Count == 0)
    {
        Pass("no file under the real logs\\ is new, modified or deleted.");
    }
    else
    {
        Fail($"{changedLogFiles.Count} file(s) changed under the real logs\\: {string.Join(", ", changedLogFiles)}", "File(s) under the owner's real logs\\ changed during the run");
    }

    var changedCacheFiles = ChangedFiles(cacheBefore, cacheAfter);
    Console.WriteLine($"    {realCacheDir}: before={cacheBefore.Count} file(s) after={cacheAfter.Count} file(s)");
    if (changedCacheFiles.Count == 0)
    {
        Pass("no file under the real cache\\ is new, modified or deleted.");
    }
    else
    {
        Fail($"{changedCacheFiles.Count} file(s) changed under the real cache\\: {string.Join(", ", changedCacheFiles)}", "File(s) under the owner's real cache\\ changed during the run");
    }
}

Console.WriteLine("\n=== SUMMARY ===");
if (skips.Count > 0)
{
    Console.WriteLine($"SKIPPED: {skips.Count} check(s) not run:");
    foreach (var s in skips)
    {
        Console.WriteLine($"  - {s}");
    }
}
if (failures.Count == 0)
{
    Console.WriteLine("PASS: All non-skipped verification checks succeeded.");
    return 0;
}
Console.WriteLine($"FAIL: {failures.Count} check(s) failed:");
foreach (var f in failures)
{
    Console.WriteLine($"  - {f}");
}
return 1;

// ---- Guard: LocalApplicationData scan ----

void RunLocalAppDataGuard()
{
    // M12g-amend §A1.4: a directory-tree scan, not a hardcoded file list (M6c's Part 0 lesson —
    // a hardcoded list leaves new files uncovered). The only two files allowed to contain the
    // needle are DeviceRegistry.cs (DefaultRootPath, 1 occurrence) and ToolLocator.cs (its two
    // Android SDK candidates, 2 occurrences) — every other file, and any unexpected COUNT in
    // those two files, is a FAIL.
    const string Needle = "GetFolderPath(Environment.SpecialFolder.LocalApplicationData";

    var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory());
    var desktopSrcDir = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop");
    if (!Directory.Exists(desktopSrcDir))
    {
        Fail($"cannot scan — {desktopSrcDir} does not exist.", "LocalAppData guard could not find DESKTOP\\Linc.Desktop to scan");
        return;
    }

    var deviceRegistryPath = Path.Combine(desktopSrcDir, "Services", "DeviceRegistry.cs");
    var toolLocatorPath = Path.Combine(desktopSrcDir, "Services", "ToolLocator.cs");

    var offenders = new List<string>();
    var deviceRegistryCount = 0;
    var toolLocatorCount = 0;

    foreach (var file in Directory.EnumerateFiles(desktopSrcDir, "*.cs", SearchOption.AllDirectories))
    {
        var text = File.ReadAllText(file);
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(Needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += Needle.Length;
        }
        if (count == 0)
        {
            continue;
        }

        if (string.Equals(file, deviceRegistryPath, StringComparison.OrdinalIgnoreCase))
        {
            deviceRegistryCount = count;
        }
        else if (string.Equals(file, toolLocatorPath, StringComparison.OrdinalIgnoreCase))
        {
            toolLocatorCount = count;
        }
        else
        {
            offenders.Add($"{Path.GetRelativePath(repoRoot, file)} ({count} occurrence(s))");
        }
    }

    if (offenders.Count == 0)
    {
        Pass("no file outside DeviceRegistry.cs/ToolLocator.cs resolves LocalApplicationData directly.");
    }
    else
    {
        Fail($"LocalApplicationData resolved outside the sanctioned sites: {string.Join("; ", offenders)}",
            "A production file bypasses IDeviceRegistry.RootPath and resolves LocalApplicationData directly (the D-057/D-058 hole reopened)");
    }

    if (deviceRegistryCount == 1)
    {
        Pass("DeviceRegistry.cs resolves LocalApplicationData exactly once (DefaultRootPath).");
    }
    else
    {
        Fail($"DeviceRegistry.cs resolves LocalApplicationData {deviceRegistryCount} time(s), expected exactly 1 (DefaultRootPath).",
            "DeviceRegistry.cs's sanctioned LocalApplicationData call count changed");
    }

    if (toolLocatorCount == 2)
    {
        Pass("ToolLocator.cs resolves LocalApplicationData exactly twice (its two Android SDK candidates).");
    }
    else
    {
        Fail($"ToolLocator.cs resolves LocalApplicationData {toolLocatorCount} time(s), expected exactly 2 (its two Android SDK candidates).",
            "ToolLocator.cs's sanctioned LocalApplicationData call count changed");
    }
}

// ---- Half one ----

void RunHalfOne()
{
    var populatedFixture = Path.Combine(Path.GetTempPath(), "Linc_cleanroomsim_populated_" + Guid.NewGuid().ToString("N"));
    var emptyFixture = Path.Combine(Path.GetTempPath(), "Linc_cleanroomsim_empty_" + Guid.NewGuid().ToString("N"));
    var populatedBin = Path.Combine(populatedFixture, "SCRCPY", "Linc.scrcpy", "bin");
    var emptyBin = Path.Combine(emptyFixture, "SCRCPY", "Linc.scrcpy", "bin");
    // packagesim check 7's approach, reused rather than reinvented: stub files, existence only —
    // FindAdb/FindScrcpy/FindScrcpyIcon only check File.Exists, never content.
    Directory.CreateDirectory(populatedBin);
    Directory.CreateDirectory(emptyBin); // exists, but deliberately empty

    File.WriteAllBytes(Path.Combine(populatedBin, "adb.exe"), [0]);
    File.WriteAllBytes(Path.Combine(populatedBin, "scrcpy.exe"), [0]);
    File.WriteAllBytes(Path.Combine(populatedBin, "scrcpy-server"), [0]);
    File.WriteAllBytes(Path.Combine(populatedBin, "linc.ico"), [0]);

    var originalPath = Environment.GetEnvironmentVariable("PATH");
    var originalAndroidHome = Environment.GetEnvironmentVariable("ANDROID_HOME");
    var originalAndroidSdkRoot = Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT");
    var originalLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
    var originalBaseDirectory = AppContext.BaseDirectory;

    try
    {
        Environment.SetEnvironmentVariable("PATH", @"C:\Windows\system32;C:\Windows");
        Environment.SetEnvironmentVariable("ANDROID_HOME", null);
        Environment.SetEnvironmentVariable("ANDROID_SDK_ROOT", null);
        var fakeLocalAppData = Path.Combine(Path.GetTempPath(), "Linc_cleanroomsim_fakelocalappdata_" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("LOCALAPPDATA", fakeLocalAppData);

        // FINDING (verified interactively before writing this, same "verify before implementing"
        // spirit as packagesim check 7's own comment, and M12e's A2 diagnosis): on this machine,
        // .NET's Environment.GetFolderPath(LocalApplicationData) on Windows resolves via
        // SHGetKnownFolderPath against the HKCU "...\User Shell Folders\Local AppData" registry
        // value, which is stored FULLY EXPANDED (confirmed: no %USERPROFILE% token to intercept
        // either) — not via the LOCALAPPDATA environment variable at all. Setting LOCALAPPDATA
        // here does NOT redirect it, neither for this process nor for a genuine separate child
        // process (both verified). This is why M12g added --data-root (Part A) instead of trying
        // to fix this: see the GUARD section above and Half Two below, which use it instead of
        // relying on this environment variable at all.
        var redirectWorks = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) == fakeLocalAppData;
        Console.WriteLine($"[A1] LOCALAPPDATA env-var redirection on this machine: {(redirectWorks ? "WORKS" : "DOES NOT WORK")} (Environment.GetFolderPath ignores the override: {!redirectWorks}).");

        Console.WriteLine("\n[A1.1] Real ToolLocator lookups resolve the exact bundled paths from a fixture publish tree...");
        AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", populatedFixture + Path.DirectorySeparatorChar);

        var expectedAdb = Path.Combine(populatedBin, "adb.exe");
        var expectedScrcpy = Path.Combine(populatedBin, "scrcpy.exe");
        var expectedIcon = Path.Combine(populatedBin, "linc.ico");

        var resolvedAdb = ToolLocator.FindAdb();
        var resolvedScrcpy = ToolLocator.FindScrcpy();
        var resolvedIcon = ToolLocator.FindScrcpyIcon();

        if (resolvedAdb == expectedAdb)
        {
            Pass($"FindAdb() resolved {resolvedAdb} — the fixture's bundled adb.exe, exactly.");
        }
        else
        {
            Fail($"FindAdb() resolved {resolvedAdb ?? "(null)"}, expected exactly {expectedAdb}.", "FindAdb() did not resolve the bundled adb from a fixture publish tree");
        }

        if (resolvedScrcpy == expectedScrcpy)
        {
            Pass($"FindScrcpy() resolved {resolvedScrcpy} — the fixture's bundled scrcpy.exe, exactly.");
        }
        else
        {
            Fail($"FindScrcpy() resolved {resolvedScrcpy ?? "(null)"}, expected exactly {expectedScrcpy}.", "FindScrcpy() did not resolve the bundled scrcpy from a fixture publish tree");
        }

        if (resolvedIcon == expectedIcon)
        {
            Pass($"FindScrcpyIcon() resolved {resolvedIcon} — the fixture's bundled linc.ico, exactly.");
        }
        else
        {
            Fail($"FindScrcpyIcon() resolved {resolvedIcon ?? "(null)"}, expected exactly {expectedIcon}.", "FindScrcpyIcon() did not resolve the bundled icon from a fixture publish tree");
        }

        Console.WriteLine("\n[A1.2] Real ToolLocator lookups fail cleanly (return null) against an empty bundle...");
        AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", emptyFixture + Path.DirectorySeparatorChar);

        var emptyIconResult = ToolLocator.FindScrcpyIcon();
        var emptyScrcpyResult = ToolLocator.FindScrcpy();
        var emptyAdbResult = ToolLocator.FindAdb();

        // FindScrcpyIcon has no PATH/SDK fallback at all — fully controllable, always testable.
        if (emptyIconResult is null)
        {
            Pass("FindScrcpyIcon() returned null against an empty bundle (it has no fallback beyond the bundled/dev-tree candidates).");
        }
        else
        {
            Fail($"FindScrcpyIcon() returned {emptyIconResult} against an empty bundle — expected null.", "FindScrcpyIcon() did not fail cleanly on an empty bundle");
        }

        // FindScrcpy falls back to a PATH scan and a WinGet package scan. PATH is genuinely
        // sanitised for this process (Environment.GetEnvironmentVariable reads it directly, no
        // OS-level indirection like GetFolderPath has), and this dev machine has no
        // %LOCALAPPDATA%\Microsoft\WinGet\Packages\Genymobile.scrcpy* folder (checked
        // interactively before writing this) — so null is a fully-controlled, honest expectation.
        if (emptyScrcpyResult is null)
        {
            Pass("FindScrcpy() returned null against an empty bundle with PATH sanitised and no winget package present.");
        }
        else
        {
            Fail($"FindScrcpy() returned {emptyScrcpyResult} against an empty bundle — expected null (the PATH or winget scan found something unexpected).", "FindScrcpy() did not fail cleanly on an empty bundle");
        }

        // FindAdb's final fallback is Environment.GetFolderPath(LocalApplicationData)\Android\Sdk\
        // platform-tools\adb.exe, which (per the redirect finding above) CANNOT be sanitised on
        // this machine, and this specific dev machine has a real installed Android SDK there
        // (GUIDE.md/GUARDRAILS.md "Environment facts": %LOCALAPPDATA%\Android\Sdk\platform-tools).
        // A strict null assertion here would be asserting something this machine cannot produce —
        // that is an environment limit, not a ToolLocator defect. Reported honestly as a SKIP
        // rather than weakened silently or asserted as a false failure.
        var realSdkAdb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe");
        if (emptyAdbResult is null)
        {
            Pass("FindAdb() returned null against an empty bundle.");
        }
        else if (!redirectWorks && emptyAdbResult == realSdkAdb && File.Exists(realSdkAdb))
        {
            Skip($"FindAdb()'s clean-failure case cannot be proven on this machine: with the bundle empty it correctly fell through to the REAL installed Android SDK at {realSdkAdb}, because LOCALAPPDATA redirection does not work here (see the [A1] finding above) — this is an environment limit, not a ToolLocator defect.");
        }
        else
        {
            Fail($"FindAdb() returned {emptyAdbResult} against an empty bundle — expected null or the known real-SDK fallback path.", "FindAdb() resolved something unexpected against an empty bundle");
        }
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", originalPath);
        Environment.SetEnvironmentVariable("ANDROID_HOME", originalAndroidHome);
        Environment.SetEnvironmentVariable("ANDROID_SDK_ROOT", originalAndroidSdkRoot);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocalAppData);
        AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", originalBaseDirectory);
        try { Directory.Delete(populatedFixture, recursive: true); } catch (IOException) { }
        try { Directory.Delete(emptyFixture, recursive: true); } catch (IOException) { }
    }
}

// ---- Half two ----

void RunHalfTwo()
{
    var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory());
    var desktopProjectDir = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop");
    var publishDir = Path.Combine(desktopProjectDir, "bin", "x64", "Release", "net8.0-windows10.0.19041.0", "win-x64", "publish");
    var exePath = Path.Combine(publishDir, "Linc.Desktop.exe");

    if (!Directory.Exists(publishDir) || !File.Exists(exePath))
    {
        Skip($"no publish output at {publishDir} — run the Beta publish first " +
             "(-t:Publish -p:PublishProfile=Beta -p:Configuration=Release -p:Platform=x64), then re-run cleanroomsim. Not counted as a pass or a failure.");
        return;
    }

    if (Process.GetProcessesByName("Linc.Desktop").Length > 0)
    {
        Skip("a Linc.Desktop process is already running (single-instance mutex, Local\\LincDesktopSingleInstance) — " +
             "a child launched now would exit immediately, measuring the mutex, not the package. Close Linc and re-run. Not counted as a pass or a failure.");
        return;
    }

    // B1 (M12g-amend): Half One's LOCALAPPDATA-redirect finding is kept as a documented guard —
    // re-probed here so a stale finding can never silently go unnoticed — but it no longer GATES
    // the launch. M12g Part A gave the app a real, injectable root (--data-root); the launch
    // below uses that and does not depend on the environment variable working at all.
    var probeValue = Path.Combine(Path.GetTempPath(), "Linc_cleanroomsim_redirect_probe_" + Guid.NewGuid().ToString("N"));
    var originalLocalAppDataProbe = Environment.GetEnvironmentVariable("LOCALAPPDATA");
    bool envRedirectWorks;
    try
    {
        Environment.SetEnvironmentVariable("LOCALAPPDATA", probeValue);
        envRedirectWorks = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) == probeValue;
    }
    finally
    {
        Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocalAppDataProbe);
    }
    Console.WriteLine($"    [guard, informational] LOCALAPPDATA env-var redirection on this machine: {(envRedirectWorks ? "now works" : "still does not work")} — the launch below uses --data-root instead and does not depend on this.");

    // B2: launch the published exe with the store root injected via --data-root, sanitised
    // environment (PATH/ANDROID_HOME/ANDROID_SDK_ROOT — the M12c class of defect).
    var tempRoot = Path.Combine(Path.GetTempPath(), "Linc_cleanroomsim_launch_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempRoot);
    Process? child = null;
    var launchStartUtc = DateTime.UtcNow;
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"--data-root \"{tempRoot}\"",
            UseShellExecute = false,
            WorkingDirectory = publishDir,
        };
        psi.EnvironmentVariables["PATH"] = @"C:\Windows\system32;C:\Windows";
        psi.EnvironmentVariables.Remove("ANDROID_HOME");
        psi.EnvironmentVariables.Remove("ANDROID_SDK_ROOT");

        child = Process.Start(psi);

        // M4b Part 0: a FIXED 5s sleep here was a false-failure generator — it failed
        // deterministically on a loaded machine (M4a's session had Gradle running concurrently)
        // even though the redirect itself was fine, and a harness that cries wolf destroys the
        // trust the whole regime depends on. Poll for the log file on a short interval up to a
        // generous ceiling instead, and report the real elapsed time either way.
        var redirectedLogsDir = Path.Combine(tempRoot, "logs");
        var settlePollCeiling = TimeSpan.FromSeconds(30);
        var settlePollStart = DateTime.UtcNow;
        string[] ndjsonFiles = [];
        while (DateTime.UtcNow - settlePollStart < settlePollCeiling)
        {
            if (child is not { HasExited: false }) break;
            ndjsonFiles = Directory.Exists(redirectedLogsDir) ? Directory.GetFiles(redirectedLogsDir, "*.ndjson") : [];
            if (ndjsonFiles.Length > 0) break;
            Thread.Sleep(250);
        }
        var settleElapsed = DateTime.UtcNow - settlePollStart;

        var stillAlive = child is { HasExited: false };
        if (stillAlive)
        {
            Pass($"the published exe is still alive {settleElapsed.TotalSeconds:F1}s after launch (settle poll).");
        }
        else
        {
            Fail($"the published exe exited within {settleElapsed.TotalSeconds:F1}s of launch (exit code {(child?.HasExited == true ? child.ExitCode : "?")}).", "Published exe did not survive the settle poll");
        }

        var crashBesideExe = Path.Combine(publishDir, StartupCrashLogger.LogFileName);
        var crashUnderRedirectedRoot = Path.Combine(tempRoot, StartupCrashLogger.LogFileName);
        if (!File.Exists(crashBesideExe) && !File.Exists(crashUnderRedirectedRoot))
        {
            Pass($"no {StartupCrashLogger.LogFileName} was written beside the exe or under the redirected root.");
        }
        else
        {
            Fail("a startup crash log was written — the published exe hit an exception on launch.", "Published exe wrote a startup crash log");
        }

        // B2.4 / the point of the whole exercise: the redirect actually took effect.
        //
        // FINDING, corrected from the task's literal wording (reported in full in the session
        // report): an injected --data-root becomes IDeviceRegistry.RootPath DIRECTLY — DeviceRegistry(string?
        // rootPath) assigns RootPath = rootPath with no "Linc" subfolder appended (that segment is
        // baked into DefaultRootPath itself, not added by the constructor). So the redirected tree
        // is "<tempRoot>\...", never "<tempRoot>\Linc/...". Verified empirically with a manual
        // launch before writing this assertion.
        //
        // Second finding: DeviceRegistry's constructor only READS settings.json if it already
        // exists; nothing calls Persist() during a bare startup with no pairing/settings change,
        // so a fresh temp root never gets a settings.json written into it — asserting its
        // existence would be asserting something the production code never does. The equivalent,
        // tighter proof used instead: LogService's LogFolderPath is now built from the SAME
        // DI-resolved IDeviceRegistry.RootPath (M12g-amend Part A0), and App.OnLaunched's A3 log
        // line echoes the exact --data-root value as its very first write — before window
        // activation, so it lands well inside the settle poll above. This ties the check to the
        // actually-constructed registry instance, not just the parsed command line: if the DI
        // registration reverts to the parameterless DeviceRegistry (negative proof #2), LogService
        // resolves the REAL owner root instead and this file never appears under tempRoot at all.
        //
        // ndjsonFiles here is whatever the settle poll above found (or didn't) — polled up to
        // settlePollCeiling rather than read after one fixed sleep (M4b Part 0).
        // The ndjson file holds raw JSON text, so backslashes in tempRoot are escaped ('\\') —
        // matched against the JsonSerializer.Serialize(entry) call in LogService.AppendToFile.
        var redirectLine = $"Store root redirected via --data-root: {tempRoot.Replace(@"\", @"\\")}";
        string? logText = ndjsonFiles.Length > 0 ? string.Join("\n", ndjsonFiles.Select(File.ReadAllText)) : null;

        if (logText is not null && logText.Contains(redirectLine, StringComparison.Ordinal))
        {
            Pass($"the app's own log under the redirected root records \"{redirectLine}\" — --data-root actually redirected the store root, tied to the real DI-constructed IDeviceRegistry.RootPath (found after {settleElapsed.TotalSeconds:F1}s of polling).");
        }
        else if (logText is not null)
        {
            Fail($"the app log under {redirectedLogsDir} exists but does not contain \"{redirectLine}\".",
                "The redirected-root log line is missing or does not match — the redirect did not take effect as expected");
        }
        else
        {
            Fail($"no app log was found under {redirectedLogsDir} after polling for {settleElapsed.TotalSeconds:F1}s (ceiling {settlePollCeiling.TotalSeconds:F0}s).",
                "No log file appeared under the redirected root — the redirect did not take effect (or LogService still resolves the real store)");
        }

        var redirectedDbPath = Path.Combine(tempRoot, "linc.db");
        if (File.Exists(redirectedDbPath))
        {
            Pass($"{redirectedDbPath} exists too — LincStore's root followed the same redirect.");
        }
        else
        {
            Skip($"{redirectedDbPath} had not appeared within the {settleElapsed.TotalSeconds:F1}s settle poll (LincStore.EnsureSchemaAsync is fire-and-forget on a background task and is not guaranteed to finish this quickly) — not counted as a pass or a failure.");
        }

        if (logText is not null && logText.Contains(@"SCRCPY\Linc.scrcpy\bin\adb.exe", StringComparison.OrdinalIgnoreCase))
        {
            Pass("the redirected app log records an adb path inside the bundle.");
        }
        else if (logText is not null)
        {
            Skip("the app log exists but does not record which adb it resolved — cannot assert the bundled-adb claim without adding production logging (out of scope). Reported honestly rather than asserted.");
        }
        else
        {
            Skip($"no app log was found under the redirected root after polling for {settleElapsed.TotalSeconds:F1}s — cannot assert the bundled-adb claim.");
        }
    }
    finally
    {
        // CloseMainWindow hides the app to the tray; it will never exit on its own (BRAIN), so
        // Kill() rather than a graceful shutdown. Kill() only sends the termination request —
        // WaitForExit afterwards (bounded, so a stuck process can't hang the harness) lets the
        // OS actually release linc.db/log file handles before the delete below, or the delete
        // loses the race and silently leaves the temp root behind.
        try
        {
            child?.Kill(entireProcessTree: true);
            child?.WaitForExit(3000);
        }
        catch (Exception) { }
        try { Directory.Delete(tempRoot, recursive: true); } catch (IOException) { }

        // Cleanup: delete a crash log this run's own launch created beside the exe — never one
        // that predates this run (a genuine leftover from an earlier failure is not ours to
        // silently erase).
        var crashBesideExe = Path.Combine(publishDir, StartupCrashLogger.LogFileName);
        if (File.Exists(crashBesideExe) && File.GetLastWriteTimeUtc(crashBesideExe) >= launchStartUtc)
        {
            try { File.Delete(crashBesideExe); } catch (IOException) { }
        }
    }
}

// ---- helpers ----

static string FindRepoRoot(string start)
{
    static string? Marker(string dir) =>
        File.Exists(Path.Combine(dir, "DESKTOP", "Linc.Desktop", "Linc.Desktop.csproj"))
            ? dir
            : File.Exists(Path.Combine(dir, "Linc", "DESKTOP", "Linc.Desktop", "Linc.Desktop.csproj"))
                ? Path.Combine(dir, "Linc")
                : null;

    for (var current = start; current != null; current = Directory.GetParent(current)?.FullName)
    {
        if (Marker(current) is { } hit)
        {
            return hit;
        }
    }
    return start;
}
