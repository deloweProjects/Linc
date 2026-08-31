// Verification harness for M12b Part A (startup crash visibility). Two layers:
//
//   1. Calls the REAL Services\StartupCrashLogger.cs (compiled verbatim, see startupsim.csproj)
//      to prove the pure log-line formatter and the file writer actually work — not a harness
//      model of them (GUIDE.md §4.1).
//   2. CRUDE source-text checks against DESKTOP\Linc.Desktop\App.xaml.cs, because the handler
//      subscriptions and the OnLaunched try/catch live inside a WinUI Application subclass this
//      console cannot host. Ordering is the whole point (A2.1: subscribe before any service is
//      touched), so the check asserts the three subscriptions sit BEFORE the ServiceCollection
//      line in source-line order, not just that they exist somewhere in the file.
//
//   dotnet run --project tools/startupsim
//
// Writes only under a throwaway temp directory (Path.GetTempPath()/…) — never %LOCALAPPDATA%\Linc,
// so D-057 does not apply and there is nothing here that can touch the owner's real data.

using System.Text.RegularExpressions;
using Linc.Desktop.Services;

Console.WriteLine("=== Linc Startup Crash-Visibility Verification Harness (startupsim) ===");

var failures = new List<string>();
const int Total = 5;

void Check(int index, bool ok, string passText, string failText, string failure)
{
    if (ok)
    {
        Console.WriteLine($"    [{index}/{Total}] PASS: {passText}");
    }
    else
    {
        Console.WriteLine($"    [{index}/{Total}] FAIL: {failText}");
        failures.Add(failure);
    }
}

void CheckSub(bool ok, string passText, string failText, string failure)
{
    if (ok)
    {
        Console.WriteLine($"        PASS: {passText}");
    }
    else
    {
        Console.WriteLine($"        FAIL: {failText}");
        failures.Add(failure);
    }
}

var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory());
var appXamlCsPath = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "App.xaml.cs");

// ---------------------------------------------------------------------------------------
// 1. The REAL pure formatter produces a line containing timestamp, type, message and stack.
// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n[1/{Total}] StartupCrashLogger.FormatLogLine (real production code) formats a crash line...");
try
{
    var sectionStart = failures.Count;
    var timestamp = new DateTimeOffset(2026, 8, 6, 13, 4, 5, TimeSpan.FromHours(-7));
    var line = StartupCrashLogger.FormatLogLine(timestamp, "System.InvalidOperationException", "boom: something specific broke", "   at Linc.Desktop.App.OnLaunched() in App.xaml.cs:line 108");

    CheckSub(Regex.IsMatch(line, @"\[2026-08-06 13:04:05\.000"),
        "line starts with a formatted timestamp matching the value passed in.",
        $"timestamp not found/formatted as expected in: {line[..Math.Min(60, line.Length)]}...",
        "FormatLogLine: timestamp missing or malformed");
    CheckSub(line.Contains("System.InvalidOperationException", StringComparison.Ordinal),
        "line contains the exception type.",
        "line does not contain the exception type.",
        "FormatLogLine: exception type missing");
    CheckSub(line.Contains("boom: something specific broke", StringComparison.Ordinal),
        "line contains the exception message.",
        "line does not contain the exception message.",
        "FormatLogLine: exception message missing");
    CheckSub(line.Contains("Linc.Desktop.App.OnLaunched", StringComparison.Ordinal),
        "line contains the stack trace.",
        "line does not contain the stack trace.",
        "FormatLogLine: stack trace missing");

    Check(1, failures.Count == sectionStart,
        "the real formatter's output contains timestamp, type, message and stack trace.",
        "the real formatter's output is missing one or more required fields (see sub-failures above).",
        "FormatLogLine output incomplete");
}
catch (Exception ex)
{
    Console.WriteLine($"    [1/{Total}] FAIL: FormatLogLine threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"FormatLogLine threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 2. The REAL file writer appends (does not overwrite) to a throwaway temp directory.
// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n[2/{Total}] StartupCrashLogger.AppendCrash (real production code) writes/appends to disk...");
var tempRoot = Path.Combine(Path.GetTempPath(), "linc-startupsim-" + Guid.NewGuid().ToString("N"));
try
{
    var sectionStart = failures.Count;
    Directory.CreateDirectory(tempRoot);
    var logPath = Path.Combine(tempRoot, StartupCrashLogger.LogFileName);

    CheckSub(!File.Exists(logPath),
        "no log file exists yet in the fresh temp root (sanity check).",
        "a log file already exists before the first crash — the temp root is not clean.",
        "AppendCrash: temp root not clean before first write");

    StartupCrashLogger.AppendCrash(tempRoot, new InvalidOperationException("first crash"));
    var afterFirst = File.Exists(logPath) ? File.ReadAllText(logPath) : "";
    CheckSub(afterFirst.Contains("first crash", StringComparison.Ordinal),
        $"{StartupCrashLogger.LogFileName} was created beside the given directory and contains the first crash.",
        $"{StartupCrashLogger.LogFileName} missing or missing 'first crash' after the first AppendCrash call.",
        "AppendCrash: first write missing/incorrect");

    StartupCrashLogger.AppendCrash(tempRoot, new InvalidOperationException("second crash"));
    var afterSecond = File.Exists(logPath) ? File.ReadAllText(logPath) : "";
    CheckSub(afterSecond.Contains("first crash", StringComparison.Ordinal) && afterSecond.Contains("second crash", StringComparison.Ordinal),
        "a second crash APPENDS — both crashes are present in the file.",
        "a second crash overwrote the file instead of appending (one of the two crash messages is missing).",
        "AppendCrash: second write overwrote instead of appending");

    Check(2, failures.Count == sectionStart,
        "AppendCrash writes to disk beside the given directory and appends across multiple crashes.",
        "AppendCrash's on-disk behaviour is wrong (see sub-failures above).",
        "AppendCrash file behaviour incorrect");
}
catch (Exception ex)
{
    Console.WriteLine($"    [2/{Total}] FAIL: AppendCrash threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"AppendCrash threw: {ex.GetType().Name}: {ex.Message}");
}
finally
{
    try { Directory.Delete(tempRoot, recursive: true); } catch { /* best-effort cleanup */ }
}

// ---------------------------------------------------------------------------------------
// 3. CRUDE: App.xaml.cs registers all three unhandled-exception surfaces.
// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n[3/{Total}] CRUDE: App.xaml.cs subscribes to all three unhandled-exception surfaces...");
string appXamlCs = "";
try
{
    if (!File.Exists(appXamlCsPath))
    {
        Check(3, false, "found App.xaml.cs.", $"could not locate {appXamlCsPath}; the crude check cannot run.", "App.xaml.cs not found");
    }
    else
    {
        appXamlCs = File.ReadAllText(appXamlCsPath);
        var hasAppDomain = appXamlCs.Contains("AppDomain.CurrentDomain.UnhandledException +=", StringComparison.Ordinal);
        CheckSub(hasAppDomain,
            "subscribes to AppDomain.CurrentDomain.UnhandledException.",
            "no subscription to AppDomain.CurrentDomain.UnhandledException found.",
            "App.xaml.cs missing AppDomain.UnhandledException subscription");
        var hasTaskScheduler = appXamlCs.Contains("TaskScheduler.UnobservedTaskException +=", StringComparison.Ordinal);
        CheckSub(hasTaskScheduler,
            "subscribes to TaskScheduler.UnobservedTaskException.",
            "no subscription to TaskScheduler.UnobservedTaskException found.",
            "App.xaml.cs missing TaskScheduler.UnobservedTaskException subscription");
        var hasXamlUnhandled = appXamlCs.Contains("UnhandledException += OnXamlUnhandledException", StringComparison.Ordinal);
        CheckSub(hasXamlUnhandled,
            "subscribes to WinUI's Application.UnhandledException.",
            "no subscription to Application.UnhandledException found.",
            "App.xaml.cs missing Application.UnhandledException subscription");

        Check(3, hasAppDomain && hasTaskScheduler && hasXamlUnhandled,
            "all three unhandled-exception surfaces are subscribed in App.xaml.cs.",
            "at least one unhandled-exception surface is not subscribed (see sub-failures above).",
            "App.xaml.cs missing one or more crash-handler subscriptions");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    [3/{Total}] FAIL: App.xaml.cs scan threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"App.xaml.cs scan threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 4. CRUDE + ORDERED: the three subscriptions sit in the CONSTRUCTOR, ABOVE the first service
//    construction (`Services = new ServiceCollection()`) — ordering is the whole point (A2.1).
//    A future session moving a handler below the service wiring must fail this, not just a
//    "does the text exist anywhere" scan.
// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n[4/{Total}] CRUDE: the three subscriptions sit ABOVE ServiceCollection construction (registration order)...");
try
{
    var sectionStart = failures.Count;
    if (string.IsNullOrEmpty(appXamlCs))
    {
        Check(4, false, "App.xaml.cs was readable.", "App.xaml.cs was not readable in check 3; ordering cannot be verified.", "App.xaml.cs unreadable for ordering check");
    }
    else
    {
        var idxAppDomain = appXamlCs.IndexOf("AppDomain.CurrentDomain.UnhandledException +=", StringComparison.Ordinal);
        var idxTaskScheduler = appXamlCs.IndexOf("TaskScheduler.UnobservedTaskException +=", StringComparison.Ordinal);
        var idxXamlUnhandled = appXamlCs.IndexOf("UnhandledException += OnXamlUnhandledException", StringComparison.Ordinal);
        var idxServices = appXamlCs.IndexOf("Services = new ServiceCollection()", StringComparison.Ordinal);
        var idxInitializeComponent = appXamlCs.IndexOf("InitializeComponent();", StringComparison.Ordinal);

        var allFound = idxAppDomain >= 0 && idxTaskScheduler >= 0 && idxXamlUnhandled >= 0 && idxServices >= 0 && idxInitializeComponent >= 0;
        CheckSub(allFound,
            "located all three subscription lines plus InitializeComponent() and the ServiceCollection line.",
            "one or more anchor lines were not found (a subscription, InitializeComponent(), or the ServiceCollection line) — cannot check ordering.",
            "Ordering check: one or more anchor lines not found");

        if (allFound)
        {
            var beforeServices = idxAppDomain < idxServices && idxTaskScheduler < idxServices && idxXamlUnhandled < idxServices;
            CheckSub(beforeServices,
                "all three subscriptions appear before `Services = new ServiceCollection()`.",
                "at least one subscription appears AFTER `Services = new ServiceCollection()` — a crash during DI construction would no longer be caught.",
                "A crash-handler subscription sits below service construction");

            var beforeInit = idxAppDomain < idxInitializeComponent && idxTaskScheduler < idxInitializeComponent && idxXamlUnhandled < idxInitializeComponent;
            CheckSub(beforeInit,
                "all three subscriptions appear before InitializeComponent().",
                "at least one subscription appears AFTER InitializeComponent() — a crash during XAML init would no longer be caught.",
                "A crash-handler subscription sits below InitializeComponent()");
        }

        Check(4, failures.Count == sectionStart,
            "the three subscriptions are the first thing App()'s constructor does, before InitializeComponent()/service construction.",
            "the subscription ordering is wrong (see sub-failures above) — a crash early in startup could go uncaught again.",
            "Crash-handler subscription ordering wrong");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    [4/{Total}] FAIL: ordering scan threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Ordering scan threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 5. CRUDE: OnLaunched's body is wrapped in try/catch, the catch shows a plain Win32
//    MessageBoxW (not a WinUI dialog) naming the log file, and exits rather than hanging;
//    the dialog text does not embed the raw stack trace (A2.3: plain language only).
// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n[5/{Total}] CRUDE: OnLaunched is wrapped, surfaces a plain MessageBoxW, and exits cleanly...");
try
{
    var sectionStart = failures.Count;
    if (string.IsNullOrEmpty(appXamlCs))
    {
        Check(5, false, "App.xaml.cs was readable.", "App.xaml.cs was not readable in check 3.", "App.xaml.cs unreadable for OnLaunched check");
    }
    else
    {
        var hasOnLaunchedTry = Regex.IsMatch(appXamlCs, @"OnLaunched\s*\([^)]*\)\s*\{\s*//[^\n]*\n(\s*//[^\n]*\n)*\s*try");
        CheckSub(hasOnLaunchedTry,
            "OnLaunched's body opens with a try block.",
            "OnLaunched does not appear to open with a try block (or comment-scan pattern drifted from the source).",
            "OnLaunched not wrapped in try/catch");
        var hasMessageBoxImport = appXamlCs.Contains("DllImport(\"user32.dll\"", StringComparison.Ordinal) && appXamlCs.Contains("MessageBoxW", StringComparison.Ordinal);
        CheckSub(hasMessageBoxImport,
            "a P/Invoke MessageBoxW (user32.dll) is declared and used.",
            "no P/Invoke MessageBoxW declaration/use found — a WinUI ContentDialog would depend on the framework that may have failed.",
            "No P/Invoke MessageBoxW found");
        var hasLogPathInMessage = appXamlCs.Contains("Details were written to", StringComparison.Ordinal) && appXamlCs.Contains("logPath", StringComparison.Ordinal);
        CheckSub(hasLogPathInMessage,
            "the dialog text names the log file's path.",
            "the dialog text does not appear to name the log file's path.",
            "Dialog text does not name the log path");
        var noStackInDialogMessage = !Regex.IsMatch(appXamlCs, @"message\s*=[\s\S]{0,400}?ex\.StackTrace");
        CheckSub(noStackInDialogMessage,
            "the dialog message string is not built from ex.StackTrace (no raw stack trace in the dialog).",
            "the dialog message string appears to embed ex.StackTrace directly — A2.3 requires plain language only, stack goes in the file.",
            "Dialog message embeds raw stack trace");
        var exitsCleanly = appXamlCs.Contains("Environment.Exit(", StringComparison.Ordinal);
        CheckSub(exitsCleanly,
            "the fatal path calls Environment.Exit rather than leaving the process hanging.",
            "no Environment.Exit call found on the fatal startup path.",
            "Fatal startup path does not exit cleanly");

        Check(5, failures.Count == sectionStart,
            "OnLaunched is wrapped, shows a plain-language Win32 MessageBox naming the log path, and exits.",
            "one or more OnLaunched-failure requirements are unmet (see sub-failures above).",
            "OnLaunched failure-surfacing requirements unmet");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    [5/{Total}] FAIL: OnLaunched scan threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"OnLaunched scan threw: {ex.GetType().Name}: {ex.Message}");
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

// ---- helpers ----

static string FindRepoRoot(string start)
{
    // Run from anywhere — `dotnet run` keeps the parent's CWD, which may be the workspace root
    // (yellow\) rather than the Linc\ code root. Walk up, and at each ancestor check both
    // <ancestor>\DESKTOP\... and <ancestor>\Linc/DESKTOP/... so both layouts resolve.
    static string? Marker(string dir) =>
        File.Exists(Path.Combine(dir, "DESKTOP", "Linc.Desktop", "App.xaml.cs"))
            ? dir
            : File.Exists(Path.Combine(dir, "Linc", "DESKTOP", "Linc.Desktop", "App.xaml.cs"))
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
