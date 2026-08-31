using Linc.Desktop.Services;
using Microsoft.Win32;

// M12e Part B7: verifies StartupRegistration (the "start Linc when I sign in" HKCU Run-key
// reader/writer) without ever touching the real Run key.
//
//   dotnet run --project tools/autostartsim
//
// D-057-style throwaway subkey: every registry round-trip here runs against
// HKEY_CURRENT_USER\Software\Linc/AutostartTest, deleted in a finally, so this harness cannot
// reach the real Software\Microsoft\Windows\CurrentVersion\Run entry by any path — not even if
// it is killed mid-run.

const string TestSubKeyPath = @"Software\Linc/AutostartTest";

var failures = new List<string>();

void True(bool condition, string claim)
{
    Console.WriteLine($"    {(condition ? "ok  " : "FAIL")} {claim}");
    if (!condition)
    {
        failures.Add(claim);
    }
}

Console.WriteLine("--- pure statics");
{
    var withSpace = @"C:\Program Files\Linc/Linc.Desktop.exe";
    var command = StartupRegistration.BuildRunCommand(withSpace);
    True(command == $"\"{withSpace}\" --startup", "BuildRunCommand quotes a path containing a space and appends --startup");

    True(StartupRegistration.NeedsRewrite(null, "x"), "NeedsRewrite: null existing needs a rewrite");
    True(StartupRegistration.NeedsRewrite("", "x"), "NeedsRewrite: empty existing needs a rewrite");
    True(!StartupRegistration.NeedsRewrite("\"C:\\a.exe\" --startup", "\"C:\\a.exe\" --startup"), "NeedsRewrite: identical values need no rewrite");
    True(!StartupRegistration.NeedsRewrite("\"C:\\A.EXE\" --STARTUP", "\"C:\\a.exe\" --startup"), "NeedsRewrite: case-insensitive match needs no rewrite");
    True(StartupRegistration.NeedsRewrite("\"C:\\old.exe\" --startup", "\"C:\\new.exe\" --startup"), "NeedsRewrite: a genuinely different value needs a rewrite");

    True(StartupRegistration.IsStartupLaunch(new[] { "Linc.Desktop.exe", "--startup" }), "IsStartupLaunch: matches --startup");
    True(StartupRegistration.IsStartupLaunch(new[] { "--STARTUP" }), "IsStartupLaunch: matches case-insensitively");
    True(!StartupRegistration.IsStartupLaunch(new[] { "Linc.Desktop.exe", "startup" }), "IsStartupLaunch: rejects a bare 'startup' (missing --)");
    True(!StartupRegistration.IsStartupLaunch(Array.Empty<string>()), "IsStartupLaunch: no args is not a startup launch");

    // A4 (M12g-amend §A1.4 / M12g §A1): a Run-key command must never carry --data-root. A key
    // pinned to a temp path would point the owner's real startup launch at a store that gets
    // deleted.
    var runCommand = StartupRegistration.BuildRunCommand(@"C:\Program Files\Linc/Linc.Desktop.exe");
    True(runCommand.Contains("--startup", StringComparison.Ordinal), "BuildRunCommand's output contains --startup");
    True(!runCommand.Contains("--data-root", StringComparison.OrdinalIgnoreCase), "BuildRunCommand's output never contains --data-root");
}

Console.WriteLine("--- ParseDataRoot (M12g Part A1)");
{
    True(StartupRegistration.ParseDataRoot(Array.Empty<string>()) is null, "ParseDataRoot: no args -> null");
    True(StartupRegistration.ParseDataRoot(new[] { "Linc.Desktop.exe", "--startup" }) is null, "ParseDataRoot: switch absent -> null");

    var relative = Path.Combine("temp-data-root-test", "sub");
    var expectedAbsolute = Path.GetFullPath(relative);

    True(StartupRegistration.ParseDataRoot(new[] { "--data-root", relative }) == expectedAbsolute,
        "ParseDataRoot: two-token form '--data-root <path>' resolves to an absolute path");
    True(StartupRegistration.ParseDataRoot(new[] { $"--data-root={relative}" }) == expectedAbsolute,
        "ParseDataRoot: one-token form '--data-root=<path>' resolves to an absolute path");
    True(StartupRegistration.ParseDataRoot(new[] { $"--DATA-ROOT={relative}" }) == expectedAbsolute,
        "ParseDataRoot: switch name is case-insensitive");
    True(StartupRegistration.ParseDataRoot(new[] { "--data-root" }) is null,
        "ParseDataRoot: switch present with no following value -> null (never a half-parsed path)");
    True(StartupRegistration.ParseDataRoot(new[] { "--data-root", "   " }) is null,
        "ParseDataRoot: whitespace-only value -> null");
    True(StartupRegistration.ParseDataRoot(new[] { "--data-root=" }) is null,
        "ParseDataRoot: empty '--data-root=' value -> null");
    True(StartupRegistration.ParseDataRoot(new[] { "--other-switch", "x", "--data-root", relative }) == expectedAbsolute,
        "ParseDataRoot: finds the switch regardless of position among other args");
}

Console.WriteLine("--- Enable/IsEnabled/Disable round-trip (throwaway subkey, never the real Run key)");
try
{
    var spyLog = new SpyLogService();
    var reg = new StartupRegistration(TestSubKeyPath, spyLog);
    True(!reg.IsEnabled(), "starts disabled — no throwaway key yet");

    const string exePath = @"D:\Some Folder\Linc.Desktop.exe";
    reg.Enable(exePath);
    True(reg.IsEnabled(), "enabled after Enable()");
    True(reg.CurrentValue() == StartupRegistration.BuildRunCommand(exePath), "stored value matches BuildRunCommand's quoted form");
    // M12h: a silent success is indistinguishable from a silent failure (the observability gap
    // that made the original "toggle does nothing visible" bug report hard to diagnose) — Enable()
    // must log on success too, not only in its catch block.
    True(spyLog.Entries.Any(e => e.Level == LogLevel.Info && e.Message.Contains("Enable succeeded", StringComparison.Ordinal)),
        "Enable() logs an Info line on success");

    reg.Disable();
    True(!reg.IsEnabled(), "disabled after Disable()");
    True(spyLog.Entries.Any(e => e.Level == LogLevel.Info && e.Message.Contains("Disable succeeded", StringComparison.Ordinal)),
        "Disable() logs an Info line on success");
}
catch (Exception ex)
{
    failures.Add($"registry round-trip threw {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine($"    THREW {ex.GetType().Name}: {ex.Message}");
}
finally
{
    try
    {
        Registry.CurrentUser.DeleteSubKeyTree(TestSubKeyPath, throwOnMissingSubKey: false);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"    (cleanup) failed to delete throwaway key: {ex.Message}");
    }
}

// Crude source-text check (BRAIN.md/GUIDE.md §4.1's second pattern, the M5c-3 lesson): the
// Settings toggle's behaviour lives in a view this console cannot construct or render, so assert
// against the production files' text instead of a harness-side model of them.
Console.WriteLine("--- source-text check: SettingsPage.xaml / SettingsViewModel.cs");
{
    var cwd = Directory.GetCurrentDirectory();
    var repoRoot = cwd;
    while (!Directory.Exists(Path.Combine(repoRoot, "DESKTOP")) && Directory.GetParent(repoRoot) != null)
    {
        repoRoot = Directory.GetParent(repoRoot)!.FullName;
    }

    var xamlPath = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "Views", "SettingsPage.xaml");
    var vmPath = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "ViewModels", "SettingsViewModel.cs");

    if (!File.Exists(xamlPath))
    {
        failures.Add($"Missing XAML file: {xamlPath}");
        Console.WriteLine($"    FAIL: Cannot read {xamlPath}");
    }
    else
    {
        var xaml = File.ReadAllText(xamlPath);
        var bindIndex = xaml.IndexOf("ViewModel.StartWithWindows", StringComparison.Ordinal);
        if (bindIndex < 0)
        {
            failures.Add("SettingsPage.xaml no longer binds ViewModel.StartWithWindows");
            Console.WriteLine("    FAIL: SettingsPage.xaml no longer binds ViewModel.StartWithWindows");
        }
        else
        {
            var windowStart = Math.Max(0, bindIndex - 120);
            var window = xaml.Substring(windowStart, Math.Min(240, xaml.Length - windowStart));
            True(window.Contains("Mode=TwoWay", StringComparison.Ordinal),
                "SettingsPage.xaml binds StartWithWindows TwoWay (a OneWay binding to a read-only property is legal XAML and compiles clean — M5c-2)");
        }
    }

    if (!File.Exists(vmPath))
    {
        failures.Add($"Missing view model file: {vmPath}");
        Console.WriteLine($"    FAIL: Cannot read {vmPath}");
    }
    else
    {
        var vm = File.ReadAllText(vmPath);
        var propIndex = vm.IndexOf("public bool StartWithWindows", StringComparison.Ordinal);
        if (propIndex < 0)
        {
            failures.Add("SettingsViewModel.cs no longer declares StartWithWindows");
            Console.WriteLine("    FAIL: SettingsViewModel.cs no longer declares StartWithWindows");
        }
        else
        {
            var propBody = vm.Substring(propIndex, Math.Min(800, vm.Length - propIndex));
            True(propBody.Contains("SaveStartWithWindows", StringComparison.Ordinal),
                "SettingsViewModel.cs's StartWithWindows setter calls SaveStartWithWindows (setting the preference without persisting it is a defect)");
        }
    }
}

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("ALL CHECKS PASSED");
    return 0;
}
Console.WriteLine($"{failures.Count} FAILURE(S):");
foreach (var f in failures)
{
    Console.WriteLine($"  - {f}");
}
return 1;
