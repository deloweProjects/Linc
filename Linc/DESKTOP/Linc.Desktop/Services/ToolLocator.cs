using System.IO;

namespace Linc.Desktop.Services;

/// <summary>
/// Finds bundled or installed tool binaries. Search order for each tool: a folder
/// next to the app (the bundled location used by packaged builds, M10), then
/// well-known install locations.
/// </summary>
public static class ToolLocator
{
    public static string? FindAdb()
    {
        var candidates = new[]
        {
            // Bundled first (M12c A2.1), ahead of every SDK fallback below: the bundled adb.exe
            // is the one matched to the bundled scrcpy-server, so preferring a random
            // system-installed adb of unknown version is a real correctness risk, not merely a
            // packaging one. This makes behaviour deterministic on every machine.
            Path.Combine(AppContext.BaseDirectory, "SCRCPY", "Linc.scrcpy", "bin", "adb.exe"),
            // Dev-tree fallback (M12c A2.3), mirroring FindScrcpy's below: a Debug build's
            // AppContext.BaseDirectory (DESKTOP\Linc.Desktop\bin\x64\Debug\<tfm>\) is six
            // directory levels below the repo root, so this finds the repo's own bundle instead
            // of silently falling through to a locally-installed SDK.
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "SCRCPY", "Linc.scrcpy", "bin", "adb.exe")),
            // Pre-M12c candidates, unchanged and kept last: a dev machine with no bundle must
            // still work.
            Path.Combine(AppContext.BaseDirectory, "adb", "adb.exe"),
            Path.Combine(Environment.GetEnvironmentVariable("ANDROID_HOME") ?? "", "platform-tools", "adb.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Android", "Sdk", "platform-tools", "adb.exe"),
        };
        return FirstExisting(candidates);
    }

    public static string? FindScrcpy()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "SCRCPY", "Linc.scrcpy", "bin", "scrcpy.exe"),
            // M12c A2.5: this used to climb only 5 levels, which resolves to
            // DESKTOP\SCRCPY\Linc.scrcpy\bin\scrcpy.exe — a path that has never existed (the bundle
            // is at the repo root, DESKTOP's sibling). Harmless in practice because the PATH and
            // winget scans below it cover the dev machine instead, but verified-wrong once
            // measured (see FindAdb's identical dev-tree candidate for the same fix).
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "SCRCPY", "Linc.scrcpy", "bin", "scrcpy.exe"))
        };
        // PATH (covers winget shims, chocolatey, manual installs)
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(dir.Trim(), "scrcpy.exe"));
        }
        // winget package layout: ...\WinGet\Packages\Genymobile.scrcpy_*\scrcpy-*\scrcpy.exe
        var wingetPackages = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(wingetPackages))
        {
            foreach (var package in Directory.GetDirectories(wingetPackages, "Genymobile.scrcpy*"))
            {
                candidates.AddRange(Directory.GetFiles(package, "scrcpy.exe", SearchOption.AllDirectories));
            }
        }
        return FirstExisting(candidates);
    }

    private static string? FirstExisting(IEnumerable<string> candidates)
    {
        try
        {
            return candidates.FirstOrDefault(path =>
                !string.IsNullOrWhiteSpace(path) && File.Exists(path));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static string? FindScrcpyIcon()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "SCRCPY", "Linc.scrcpy", "bin", "linc.ico"),
            Path.Combine(AppContext.BaseDirectory, "SCRCPY", "Linc.scrcpy", "linc.ico"),
            // M12c A2.5: same 5-vs-6-level fix as FindAdb/FindScrcpy's dev-tree candidates.
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "SCRCPY", "Linc.scrcpy", "bin", "linc.ico")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "SCRCPY", "Linc.scrcpy", "linc.ico"))
        };
        return FirstExisting(candidates);
    }
}
