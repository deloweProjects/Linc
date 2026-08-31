using Linc.Desktop.Services;

// Verifies PhoneSetupService's companion-APK resolution (M2b, part 1) without WinUI. ApkResolver
// is linked verbatim from the desktop and driven with an in-memory "which paths exist" predicate,
// so the beside-the-exe-first / dev-fallback / nothing-found branches are all exercised
// deterministically:
//
//   dotnet run --project tools/apkprobe
//
// It then does a real-tree check: if the desktop has actually been built, confirm the bundled
// companion.apk landed next to the exe (the csproj copy step) — the other half of "the desktop
// carries the companion".

var failures = new List<string>();

void Check(string name, bool pass, string detail)
{
    Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name} — {detail}");
    if (!pass)
    {
        failures.Add(name);
    }
}

void Info(string detail) => Console.WriteLine($"  [info] {detail}");

const string baseDir = @"C:\Program Files\Linc";
var besidePath = Path.Combine(baseDir, "Assets", "companion.apk");

Console.WriteLine("ApkResolver branch logic");

// 1. Beside the exe wins, even when the dev fallback also exists.
{
    var result = ApkResolver.Resolve(baseDir, p => p == besidePath || p.EndsWith("app-debug.apk", StringComparison.OrdinalIgnoreCase));
    Check("beside-wins", result == besidePath, result ?? "<null>");
}

// 2. Falls back to the repo's Android build output when nothing is beside the exe.
{
    var result = ApkResolver.Resolve(baseDir, p => p.EndsWith("app-debug.apk", StringComparison.OrdinalIgnoreCase));
    var ok = result is not null
        && result.EndsWith("app-debug.apk", StringComparison.OrdinalIgnoreCase)
        && Path.IsPathFullyQualified(result);
    Check("dev-fallback", ok, result ?? "<null>");
}

// 3. Returns null when neither candidate exists (desktop-only build).
{
    var result = ApkResolver.Resolve(baseDir, _ => false);
    Check("none-found", result is null, result ?? "<null>");
}

Console.WriteLine();
Console.WriteLine("Real tree");

var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
if (repoRoot is null)
{
    Info("couldn't locate the repo root from the run location; skipping tree checks.");
}
else
{
    var androidApk = Path.Combine(repoRoot,
        "ANDROID", "app", "build", "outputs", "apk", "debug", "app-debug.apk");
    Info(File.Exists(androidApk)
        ? $"Android debug APK present ({new FileInfo(androidApk).Length / 1_000_000.0:0.0} MB) — the csproj copy step has a source."
        : "Android debug APK not built yet — run ANDROID\\gradlew.bat assembleDebug to have it bundled.");

    // Any built desktop output directory that contains the exe; if one exists, the bundled APK
    // must sit beside it. Not built yet → informational, not a failure.
    var outputs = FindDesktopOutputs(repoRoot);
    if (outputs.Count == 0)
    {
        Info("desktop not built yet; skipping the bundled-beside-the-exe check.");
    }
    else
    {
        foreach (var dir in outputs)
        {
            var bundled = Path.Combine(dir, "Assets", "companion.apk");
            var rel = Path.GetRelativePath(repoRoot, dir);
            if (File.Exists(androidApk))
            {
                Check($"bundled-in-output ({rel})", File.Exists(bundled),
                    File.Exists(bundled) ? "companion.apk present" : "companion.apk MISSING beside the exe");
            }
            else
            {
                Info($"{rel}: skipped (no Android APK to bundle).");
            }
        }
    }
}

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("apkprobe: all checks green.");
    return 0;
}
Console.WriteLine($"apkprobe: {failures.Count} FAILED — {string.Join(", ", failures)}");
return 1;

// Walk up until a directory holding both DESKTOP and ANDROID is found (the repo root).
static string? FindRepoRoot(string start)
{
    var dir = new DirectoryInfo(start);
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "DESKTOP"))
            && Directory.Exists(Path.Combine(dir.FullName, "ANDROID")))
        {
            return dir.FullName;
        }
        dir = dir.Parent;
    }
    return null;
}

// Every built desktop output dir (bin/<platform>/<config>/<tfm>/) that actually holds the exe.
static List<string> FindDesktopOutputs(string repoRoot)
{
    var binRoot = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "bin");
    if (!Directory.Exists(binRoot))
    {
        return [];
    }
    return Directory.EnumerateFiles(binRoot, "Linc.Desktop.exe", SearchOption.AllDirectories)
        .Select(f => Path.GetDirectoryName(f)!)
        .Distinct()
        .ToList();
}
