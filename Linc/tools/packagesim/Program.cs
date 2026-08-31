// Verification harness for M12/M12b Part B (beta packaging). Checks 1-3 verify the packaging
// INPUTS at their source paths: the four payload sets B2.2 requires the packaged folder to carry,
// that the csproj still copies SCRCPY\Linc.scrcpy\bin\**\*, and a CRUDE source-text check (B2.4) that
// no .cs file under DESKTOP\Linc.Desktop\ contains an absolute `C:\Users\` or `D:\delowe` path —
// the D-050 class of bug (scrcpy baked in the MSYS2 prefix and failed on a clean machine) applied
// to the desktop app's own source this time.
//
// Checks 4-5 are M12b Part B's addition, closing the exact gap that let a broken package ship as
// green: M12 verified payloads at their SOURCE paths only, never in the actual `-t:Publish` OUTPUT
// (DESKTOP\Linc.Desktop\Properties\PublishProfiles\Beta.pubxml). That gap hid a real bug —
// $(TargetName).pri (the app's OWN compiled resource index: every ThemeResource in
// Themes\MaterialExpressive.xaml, incl. PrimaryContainerBrush) was never copied into publish\ by
// the Publish target (Microsoft.UI.pri/Microsoft.UI.Xaml.Controls.pri arrive via the WindowsAppSDK
// NuGet package's own Content items; the app's own .pri has no such item because that hookup only
// exists on the MSIX packaging path, and this project is WindowsPackageType=None). The result: the
// published exe threw an unhandled COMException("Cannot find a resource with the given key")
// reading Application.Current.Resources["PrimaryContainerBrush"] in HomeViewModel's constructor,
// on EVERY launch (3/3 measured) — no window, no dialog, no log, before M12b Part A existed.
// Check 5 requires an actual publish to already exist (run the Publish command first — see
// Beta.pubxml's header comment); it does not invoke MSBuild itself.
//
//   dotnet run --project tools/packagesim
//
// Touches no DeviceRegistry/LincStore and opens no real store — D-057 does not apply here.
//
// Checks 6-7 are M12c Part A's addition: Services\ToolLocator.cs shipped adb but FindAdb()
// never looked at the bundled SCRCPY\Linc.scrcpy\bin\ location, so a clean second PC that opened the
// app (past the M12b .pri fix) still failed with "Linc couldn't find its ADB engine on this
// PC." Check 6 is the M12b-style output-side assertion (adb.exe at the exact bundled path,
// inside publish\, not its source path). Check 7 calls the real ToolLocator.FindAdb() against a
// throwaway fixture directory laid out like a publish output — GUIDE.md 4.1/4.3: a harness that
// models the candidate list instead of calling the real function proves nothing, and a
// negative proof must never run through a real-data path.
//
// Check 8 is M12c Part B's addition: the VC++ redistributable DLLs (msvcp140.dll,
// vcruntime140.dll, vcruntime140_1.dll, and msvcp140_1/_2.dll if present) that
// IncludeVcRedistDllsInPublishOutput in Beta.pubxml now copies to the publish root. Per B2.5,
// a missing publish folder is a SKIP here, never a silent PASS or a hard FAIL — the DLL check
// has nothing to verify without a publish, unlike checks 5-7 which already fail hard on that
// condition for their own payloads. Check 11 is M14 Part B's addition: the default palette in
// Themes\MaterialExpressive.xaml must stay black-and-white (the M14 purple-family statics are
// pinned as absent, the new neutral ramp as present) — the guard the M14 negative proof #1
// reverts a brush against. Check 1/5 also pin M14 A3's linc.ico in the scrcpy bundle at both
// the source path and the publish output — the file the M14 negative proof #2 points at a
// missing file.

using System.Buffers.Binary;
using System.IO.Compression;
using Linc.Desktop.Services;

// Check 10 is M12i Part A2's addition: a STALENESS GUARD. Checks 5-9 verify the publish OUTPUT
// is structurally complete, but nothing before M12i required that output to be CURRENT — M4a/M4b
// landed a new Tools page, new quick controls, and a new NuGet dependency (System.Management)
// while the publish folder stayed dated 2026-08-07 21:09:52, and every check above still went
// green because a stale publish happened to satisfy every check it makes. Check 10 compares the
// publish output's newest build artifact against the newest DESKTOP\Linc.Desktop\ source file
// (**\*.cs, **\*.xaml, Linc.Desktop.csproj — deliberately NOT tools\, which never ships, and NOT
// ANDROID\, which has its own build) and FAILS when the publish predates it by more than a small
// tolerance. A missing publish is a SKIP (same as checks 8-9), not a fail: "no publish" is a
// different condition from "stale publish" per this check's own design.
//
//   Proves: the publish is not older than the code.
//   Does NOT prove: the publish is CORRECT — that is what checks 1-9 are for.

Console.WriteLine("=== Linc Packaging Verification Harness (packagesim) ===");

var failures = new List<string>();
var skips = new List<string>();
const int Total = 11;

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
var desktopProjectDir = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop");

// ---------------------------------------------------------------------------------------
// 1. The four payload sets B2.2 requires exist at their SOURCE paths (pre-build) — the inputs
//    a publish pulls from, not the build output itself (which needs a real publish to produce;
//    that path was hand-verified this session against DESKTOP\Linc.Desktop\bin\...\publish\).
// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n[1/{Total}] The four packaging payload sets exist at their source paths...");
try
{
    // 1a. Bundled scrcpy + adb (SCRCPY\Linc.scrcpy\bin\ — ~98 mingw64 DLLs, scrcpy.exe, adb.exe).
    var scrcpyBin = Path.Combine(repoRoot, "SCRCPY", "Linc.scrcpy", "bin");
    var scrcpyFiles = Directory.Exists(scrcpyBin) ? Directory.GetFiles(scrcpyBin) : [];
    CheckSub(scrcpyFiles.Length > 50,
        $"SCRCPY\\Linc.scrcpy\\bin\\ exists with {scrcpyFiles.Length} file(s) (expects ~100: scrcpy + adb + mingw64 DLLs).",
        $"SCRCPY\\Linc.scrcpy\\bin\\ has only {scrcpyFiles.Length} file(s) at {scrcpyBin} — the scrcpy bundle looks incomplete or missing.",
        "SCRCPY\\Linc.scrcpy\\bin\\ payload missing or incomplete");

    // M14 A3: the scrcpy window icon lives in the bundle. FindScrcpyIcon() resolves
    // SCRCPY\Linc.scrcpy\bin\linc.ico first; a missing file means the mirror window loses
    // its brand. This is the check the M14 negative proof #2 reverts against.
    var scrcpyIcon = Path.Combine(scrcpyBin, "linc.ico");
    CheckSub(File.Exists(scrcpyIcon),
        $"linc.ico (FindScrcpyIcon()'s first candidate) is present at {scrcpyIcon}.",
        $"linc.ico is MISSING at {scrcpyIcon} — the mirror window would launch unbranded.",
        "linc.ico missing from SCRCPY\\Linc.scrcpy\\bin\\");

    // M17a A1/A4: presence is NOT enough. The check above passes for ANY file called linc.ico —
    // it was verified during M17a by swapping in a 207-byte flat opaque black square, and every
    // packagesim check stayed green. So the mark's actual geometry is asserted here: the full
    // 7-size frame set, and transparent corners on the 16 px frame (A1's rounding + alpha, and
    // A4's "a tray icon that reads as a black blob at 16 px is a failure"). Decoding is
    // hand-rolled against the BCL only (ZLibStream) — packagesim takes no new dependency.
    var iconGeom = IconGeometry.Read(scrcpyIcon);
    CheckSub(iconGeom.Sizes.SequenceEqual(new[] { 16, 24, 32, 48, 64, 128, 256 }),
        $"linc.ico carries all 7 sizes ({string.Join('/', iconGeom.Sizes)}).",
        $"linc.ico has frames {string.Join('/', iconGeom.Sizes)} — M17a A2 requires 16/24/32/48/64/128/256.",
        "linc.ico is missing frame sizes");
    CheckSub(iconGeom.CornerAlpha16 == 0,
        $"linc.ico's 16 px frame has a fully transparent corner (alpha={iconGeom.CornerAlpha16}) — the mark is rounded and has alpha, not a square black plate.",
        $"linc.ico's 16 px frame corner has alpha={iconGeom.CornerAlpha16} — M17a A1's rounding/alpha has been lost; the tray icon is a black blob.",
        "linc.ico's rounding/alpha reverted");
    CheckSub(iconGeom.InkFraction16 is > 0.50 and < 0.95,
        $"linc.ico's 16 px frame is {iconGeom.InkFraction16:P1} opaque — an inset rounded plate (A1's 88% scale), neither edge-to-edge nor blank.",
        $"linc.ico's 16 px frame is {iconGeom.InkFraction16:P1} opaque — expected 50-95%; the artwork is either bleeding to the edge or missing.",
        "linc.ico's inset/scale reverted");

    // 1b. adb — lives inside the same scrcpy bin folder (D-023: one vendored copy, not two).
    var adbPath = Path.Combine(scrcpyBin, "adb.exe");
    CheckSub(File.Exists(adbPath),
        $"adb.exe found at {adbPath}.",
        $"adb.exe NOT found at {adbPath} — Linc's own ADB transport needs it, not just scrcpy.",
        "adb.exe payload missing");

    // 1c. The companion APK M2b injects (built by ANDROID\gradlew.bat assembleDebug; the csproj's
    //     own Content Include for it is conditional on this exact path — see csproj comment).
    var apkPath = Path.Combine(repoRoot, "ANDROID", "app", "build", "outputs", "apk", "debug", "app-debug.apk");
    CheckSub(File.Exists(apkPath),
        $"the companion APK is built at {apkPath}.",
        $"the companion APK is NOT built at {apkPath} — run `ANDROID\\gradlew.bat assembleDebug` first, " +
        "or a packaged build silently ships without onboarding's bundled APK (the csproj's Content Include is conditional and just skips it).",
        "Companion APK source missing");

    // 1d. The app's own assets (icon, tray icon).
    var assetsDir = Path.Combine(desktopProjectDir, "Assets");
    var hasIcon = File.Exists(Path.Combine(assetsDir, "AppIcon.ico"));
    var hasTrayIcon = File.Exists(Path.Combine(assetsDir, "linc.png"));
    CheckSub(hasIcon && hasTrayIcon,
        $"DESKTOP\\Linc.Desktop\\Assets\\ has AppIcon.ico and linc.png.",
        $"DESKTOP\\Linc.Desktop\\Assets\\ is missing AppIcon.ico ({hasIcon}) and/or linc.png ({hasTrayIcon}).",
        "App's own Assets\\ incomplete");

    Check(1, !failures.Any(f => f.EndsWith("missing", StringComparison.Ordinal) || f.EndsWith("incomplete", StringComparison.Ordinal)),
        "all four payload sets (scrcpy, adb, companion APK, app assets) are present at their source paths.",
        "at least one payload set is missing at its source path (see FAILs above).",
        "Packaging payload sources incomplete");
}
catch (Exception ex)
{
    Console.WriteLine($"    [1/{Total}] FAIL: payload-source scan threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Payload-source scan threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 2. CRUDE source-text check: Linc.Desktop.csproj still copies SCRCPY\Linc.scrcpy\bin\**\* to the
//    output — the wiring that makes payload set 1a actually ship, not just exist on disk.
// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n[2/{Total}] CRUDE: Linc.Desktop.csproj still copies SCRCPY\\Linc.scrcpy\\bin\\**\\*...");
try
{
    var csprojPath = Path.Combine(desktopProjectDir, "Linc.Desktop.csproj");
    if (!File.Exists(csprojPath))
    {
        Check(2, false, "found Linc.Desktop.csproj.", $"could not locate {csprojPath}; the crude check cannot run.", "Linc.Desktop.csproj not found");
    }
    else
    {
        var csproj = File.ReadAllText(csprojPath);
        var hasContentInclude = csproj.Contains(@"SCRCPY\Linc.scrcpy\bin\**\*", StringComparison.Ordinal);
        CheckSub(hasContentInclude,
            @"a Content Include for ..\..\SCRCPY\Linc.scrcpy\bin\**\* is present.",
            @"no Content Include for SCRCPY\Linc.scrcpy\bin\**\* found — the scrcpy bundle would not ship.",
            "csproj no longer copies SCRCPY\\Linc.scrcpy\\bin\\**\\*");
        var hasCopyToOutput = csproj.Contains("<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>", StringComparison.Ordinal);
        CheckSub(hasCopyToOutput,
            "at least one CopyToOutputDirectory=PreserveNewest is present (Content items actually copy).",
            "no CopyToOutputDirectory found on any Content item — items would be referenced but never copied.",
            "csproj Content items don't specify CopyToOutputDirectory");
        Check(2, hasContentInclude && hasCopyToOutput,
            "the csproj still wires up the scrcpy bundle to ship in the build output.",
            "the csproj's scrcpy packaging wiring is broken (see sub-failures above).",
            "csproj scrcpy wiring broken");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    [2/{Total}] FAIL: csproj scan threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"csproj scan threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 3. CRUDE source-text check (B2.4): no .cs file under DESKTOP\Linc.Desktop\ contains an
//    absolute `C:\Users\` or `D:\delowe` path — the D-050 class of bug (scrcpy baked in the
//    MSYS2 prefix and failed on a clean run) applied to the desktop app's own source. Comments
//    are NOT excluded here on purpose: an absolute path baked into a comment is still a sign the
//    author's environment leaked into the file, worth flagging even if it isn't executed.
// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n[3/{Total}] CRUDE: no .cs file under DESKTOP\\Linc.Desktop\\ has an absolute author-machine path...");
try
{
    var csFiles = Directory.Exists(desktopProjectDir)
        ? Directory.EnumerateFiles(desktopProjectDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToList()
        : [];
    var offenders = new List<string>();
    foreach (var file in csFiles)
    {
        var text = File.ReadAllText(file);
        if (text.Contains(@"C:\Users\", StringComparison.OrdinalIgnoreCase) ||
            text.Contains(@"D:\delowe", StringComparison.OrdinalIgnoreCase))
        {
            offenders.Add(Path.GetRelativePath(repoRoot, file));
        }
    }
    Check(3, csFiles.Count > 0 && offenders.Count == 0,
        $"scanned {csFiles.Count} .cs file(s) under DESKTOP\\Linc.Desktop\\; none contain an absolute C:\\Users\\ or D:\\delowe path.",
        offenders.Count > 0
            ? $"{offenders.Count} .cs file(s) contain an absolute author-machine path: {string.Join(", ", offenders)}."
            : $"scanned 0 .cs files — {desktopProjectDir} not found; the crude scan cannot run.",
        "A .cs file under DESKTOP\\Linc.Desktop\\ contains an absolute author-machine path");
}
catch (Exception ex)
{
    Console.WriteLine($"    [3/{Total}] FAIL: absolute-path scan threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Absolute-path scan threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 4. CRUDE source-text check: Beta.pubxml still wires $(TargetName).pri into the publish
//    output. This is the regression guard for the M12b Part B fix — without it, the app's own
//    compiled resources silently stop shipping and every published launch throws.
// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n[4/{Total}] CRUDE: Beta.pubxml still copies $(TargetName).pri into the publish output...");
try
{
    var pubxmlPath = Path.Combine(desktopProjectDir, "Properties", "PublishProfiles", "Beta.pubxml");
    if (!File.Exists(pubxmlPath))
    {
        Check(4, false, "found Beta.pubxml.", $"could not locate {pubxmlPath}; the crude check cannot run.", "Beta.pubxml not found");
    }
    else
    {
        var pubxml = File.ReadAllText(pubxmlPath);
        var hasResolvedFileToPublish = pubxml.Contains("ResolvedFileToPublish", StringComparison.Ordinal);
        CheckSub(hasResolvedFileToPublish,
            "a ResolvedFileToPublish item is present.",
            "no ResolvedFileToPublish item found — the app's own .pri has no way to reach the publish output.",
            "Beta.pubxml missing ResolvedFileToPublish for the app .pri");
        var hasTargetNamePri = pubxml.Contains("$(TargetName).pri", StringComparison.Ordinal);
        CheckSub(hasTargetNamePri,
            "the item references $(TargetName).pri specifically (the app's own compiled resource index).",
            "no reference to $(TargetName).pri found — a different file may be wired instead of the app's own resource index.",
            "Beta.pubxml not wiring $(TargetName).pri");
        Check(4, hasResolvedFileToPublish && hasTargetNamePri,
            "Beta.pubxml still wires the app's own .pri into the publish output.",
            "Beta.pubxml's .pri wiring is broken or missing (see sub-failures above) — a published build would silently ship broken again.",
            "Beta.pubxml .pri wiring broken");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    [4/{Total}] FAIL: Beta.pubxml scan threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Beta.pubxml scan threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 5. The publish OUTPUT (not source paths) actually carries what a clean-machine launch needs:
//    self-contained runtime markers, the app's own .pri, and the same four payload sets check 1
//    verified at their source paths. Requires a real `-t:Publish` to have already run (see
//    Beta.pubxml's header comment for the exact command) — this harness does not invoke MSBuild.
// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n[5/{Total}] The publish OUTPUT carries a self-contained runtime, the app's own .pri, and all four payload sets...");
try
{
    var publishDir = Path.Combine(desktopProjectDir, "bin", "x64", "Release", "net8.0-windows10.0.19041.0", "win-x64", "publish");
    if (!Directory.Exists(publishDir))
    {
        Check(5, false,
            "found a publish output folder.",
            $"no publish output at {publishDir} — run the Beta publish first (see Beta.pubxml's header comment), then re-run packagesim.",
            "No publish output to verify (run -t:Publish -p:PublishProfile=Beta first)");
    }
    else
    {
        var sectionStart = failures.Count;
        CheckSub(File.Exists(Path.Combine(publishDir, "coreclr.dll")) && File.Exists(Path.Combine(publishDir, "hostfxr.dll")),
            "coreclr.dll and hostfxr.dll are present (the publish output is genuinely self-contained, not the un-published Release\\...\\ folder).",
            "coreclr.dll and/or hostfxr.dll are missing — this is not a self-contained output (B2.3).",
            "Publish output is not self-contained");

        var appPri = Path.Combine(publishDir, "Linc.Desktop.pri");
        CheckSub(File.Exists(appPri),
            "Linc.Desktop.pri (the app's own compiled resource index) is present in the publish output.",
            "Linc.Desktop.pri is MISSING from the publish output — this is the M12b Part B root cause: " +
            "every ThemeResource lookup (PrimaryContainerBrush etc.) throws unhandled at startup on a clean machine.",
            "App's own .pri missing from publish output");

        var scrcpyBinOut = Path.Combine(publishDir, "SCRCPY", "Linc.scrcpy", "bin");
        var scrcpyOutFiles = Directory.Exists(scrcpyBinOut) ? Directory.GetFiles(scrcpyBinOut) : [];
        CheckSub(scrcpyOutFiles.Length > 50,
            $"SCRCPY\\Linc.scrcpy\\bin\\ exists in the publish output with {scrcpyOutFiles.Length} file(s).",
            $"SCRCPY\\Linc.scrcpy\\bin\\ in the publish output has only {scrcpyOutFiles.Length} file(s) at {scrcpyBinOut}.",
            "SCRCPY\\Linc.scrcpy\\bin\\ payload missing/incomplete in publish output");

        var adbOut = Path.Combine(scrcpyBinOut, "adb.exe");
        CheckSub(File.Exists(adbOut),
            $"adb.exe is present in the publish output at {adbOut}.",
            $"adb.exe is MISSING from the publish output at {adbOut}.",
            "adb.exe missing from publish output");

        var scrcpyIconOut = Path.Combine(scrcpyBinOut, "linc.ico");
        CheckSub(File.Exists(scrcpyIconOut),
            $"linc.ico (FindScrcpyIcon()'s bundled first candidate) is present in the publish output at {scrcpyIconOut}.",
            $"linc.ico is MISSING from the publish output at {scrcpyIconOut}.",
            "linc.ico missing from publish output");

        var apkOut = Path.Combine(publishDir, "Assets", "companion.apk");
        CheckSub(File.Exists(apkOut),
            "Assets\\companion.apk is present in the publish output.",
            "Assets\\companion.apk is MISSING from the publish output — onboarding's bundled install would silently have nothing to inject.",
            "companion.apk missing from publish output");

        var iconOut = Path.Combine(publishDir, "Assets", "AppIcon.ico");
        var trayOut = Path.Combine(publishDir, "Assets", "linc.png");
        CheckSub(File.Exists(iconOut) && File.Exists(trayOut),
            "Assets\\AppIcon.ico and Assets\\linc.png are present in the publish output.",
            "Assets\\AppIcon.ico and/or Assets\\linc.png are MISSING from the publish output.",
            "App's own Assets\\ incomplete in publish output");

        Check(5, failures.Count == sectionStart,
            "the publish output is self-contained and carries every payload a clean-machine launch needs.",
            "the publish output is missing something a clean-machine launch needs (see sub-failures above).",
            "Publish output incomplete");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    [5/{Total}] FAIL: publish-output scan threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Publish-output scan threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 6. M12c A3.3: adb.exe exists at the EXACT path FindAdb() will probe first, inside the
//    publish OUTPUT — not its source path (SCRCPY\Linc.scrcpy\bin\ was already checked in check 5;
//    this check specifically pins the *first-probe* bundled location the bug was in).
// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n[6/{Total}] M12c: adb.exe is at FindAdb()'s bundled first-probe path in the publish output...");
try
{
    var publishDir = Path.Combine(desktopProjectDir, "bin", "x64", "Release", "net8.0-windows10.0.19041.0", "win-x64", "publish");
    if (!Directory.Exists(publishDir))
    {
        Check(6, false,
            "found a publish output folder.",
            $"no publish output at {publishDir} — run the Beta publish first, then re-run packagesim.",
            "No publish output to verify (run -t:Publish -p:PublishProfile=Beta first)");
    }
    else
    {
        var expectedAdbPath = Path.Combine(publishDir, "SCRCPY", "Linc.scrcpy", "bin", "adb.exe");
        Check(6, File.Exists(expectedAdbPath),
            $"adb.exe is at {expectedAdbPath} — FindAdb()'s first candidate on a published layout.",
            $"adb.exe is MISSING at {expectedAdbPath} — FindAdb()'s first candidate would fall through " +
            "to a system SDK (or fail entirely on a clean machine), exactly the M12c bug.",
            "Bundled adb.exe missing from FindAdb()'s first-probe path in the publish output");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    [6/{Total}] FAIL: bundled-adb-path scan threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Bundled-adb-path scan threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 7. M12c A3.3 (real-function proof): call the REAL ToolLocator.FindAdb() — not a model of its
//    candidate list — against a throwaway fixture directory laid out like a publish output.
//    AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", ...) genuinely overrides
//    AppContext.BaseDirectory for this process (verified interactively before writing this),
//    so FindAdb() runs its real bundled-first probe against the fixture. The fixture is a fresh
//    Temp directory, never the owner's real install (GUIDE.md 4.3) — D-057 does not apply here
//    since nothing touches DeviceRegistry/LincStore. The assertion is the EXACT resolved path,
//    not merely non-null (GUIDE.md 4.4) — a loose null-check would still pass if FindAdb() fell
//    through to a real ANDROID_HOME/SDK install on the machine running this harness instead of
//    genuinely resolving the bundle.
// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n[7/{Total}] M12c: the REAL ToolLocator.FindAdb() resolves the bundle from a fixture publish layout...");
var fixtureRoot = Path.Combine(Path.GetTempPath(), "Linc_packagesim_adbfixture_" + Guid.NewGuid().ToString("N"));
var originalBaseDirectory = AppContext.BaseDirectory;
try
{
    var fixtureAdbDir = Path.Combine(fixtureRoot, "SCRCPY", "Linc.scrcpy", "bin");
    Directory.CreateDirectory(fixtureAdbDir);
    var fixtureAdbPath = Path.Combine(fixtureAdbDir, "adb.exe");
    File.WriteAllBytes(fixtureAdbPath, [0]); // FindAdb only checks existence, not content

    AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", fixtureRoot + Path.DirectorySeparatorChar);
    var resolved = ToolLocator.FindAdb();

    Check(7, resolved == fixtureAdbPath,
        $"ToolLocator.FindAdb() resolved {resolved} — the fixture's bundled adb.exe, exactly.",
        $"ToolLocator.FindAdb() resolved {resolved ?? "(null)"}, expected exactly {fixtureAdbPath} — " +
        "the bundled-first candidate in Services\\ToolLocator.cs is broken or missing.",
        "ToolLocator.FindAdb() did not resolve the bundled adb from a publish-shaped fixture");
}
catch (Exception ex)
{
    Console.WriteLine($"    [7/{Total}] FAIL: real-FindAdb fixture check threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Real-FindAdb fixture check threw: {ex.GetType().Name}: {ex.Message}");
}
finally
{
    AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", originalBaseDirectory);
    try { Directory.Delete(fixtureRoot, recursive: true); } catch { /* best-effort cleanup of a Temp scratch dir */ }
}

// ---------------------------------------------------------------------------------------
// 8. M12c Part B (B2.5): the VC++ redist DLLs are in the publish OUTPUT. A missing publish
//    folder is a SKIP (not a pass, not a fail) — there is nothing to check without one.
// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n[8/{Total}] M12c: the VC++ redistributable DLLs are in the publish output...");
try
{
    var publishDir = Path.Combine(desktopProjectDir, "bin", "x64", "Release", "net8.0-windows10.0.19041.0", "win-x64", "publish");
    if (!Directory.Exists(publishDir))
    {
        Console.WriteLine($"    [8/{Total}] SKIP: no publish output at {publishDir} — run the Beta publish first, then re-run packagesim. Not counted as a pass or a failure.");
        skips.Add("VC++ redist DLL check skipped: no publish output present");
    }
    else
    {
        var requiredDlls = new[] { "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" };
        var optionalDlls = new[] { "msvcp140_1.dll", "msvcp140_2.dll" };
        var sectionStart = failures.Count;
        foreach (var dll in requiredDlls)
        {
            var dllPath = Path.Combine(publishDir, dll);
            CheckSub(File.Exists(dllPath),
                $"{dll} is present in the publish output.",
                $"{dll} is MISSING from the publish output at {dllPath} — a beta tester without the VC++ " +
                "redistributable installed could fail to launch.",
                $"{dll} missing from publish output");
        }
        var optionalPresent = optionalDlls.Where(dll => File.Exists(Path.Combine(publishDir, dll))).ToList();
        Console.WriteLine($"        INFO: optional DLLs present: {(optionalPresent.Count > 0 ? string.Join(", ", optionalPresent) : "(none)")} " +
            $"(msvcp140_1.dll/msvcp140_2.dll ship only if the installed VS redist carries them).");
        Check(8, failures.Count == sectionStart,
            "all required VC++ redist DLLs are present in the publish output.",
            "one or more required VC++ redist DLLs are missing from the publish output (see FAILs above).",
            "VC++ redist DLLs incomplete in publish output");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    [8/{Total}] FAIL: VC++ redist DLL scan threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"VC++ redist DLL scan threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 9. M12f Part B3: cheap insurance for the exact M12b Part B defect (check 5 already asserts
//    Linc.Desktop.pri exists; this is a dedicated, minimal check kept separate so it stays cheap
//    and obviously named). A missing publish folder is a SKIP, same as checks 5-8.
// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n[9/{Total}] M12f: Linc.Desktop.pri exists in the publish output and is larger than 1 KB...");
try
{
    var publishDir = Path.Combine(desktopProjectDir, "bin", "x64", "Release", "net8.0-windows10.0.19041.0", "win-x64", "publish");
    if (!Directory.Exists(publishDir))
    {
        Console.WriteLine($"    [9/{Total}] SKIP: no publish output at {publishDir} — run the Beta publish first, then re-run packagesim. Not counted as a pass or a failure.");
        skips.Add(".pri insurance check skipped: no publish output present");
    }
    else
    {
        var appPriPath = Path.Combine(publishDir, "Linc.Desktop.pri");
        var exists = File.Exists(appPriPath);
        var size = exists ? new FileInfo(appPriPath).Length : 0;
        Check(9, exists && size > 1024,
            $"Linc.Desktop.pri is present ({size:N0} bytes).",
            exists
                ? $"Linc.Desktop.pri is present but only {size:N0} bytes (<= 1 KB) — suspiciously small for a real resource index."
                : $"Linc.Desktop.pri is MISSING from the publish output at {appPriPath} — the exact M12b Part B defect.",
            "Linc.Desktop.pri missing or suspiciously small in publish output");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    [9/{Total}] FAIL: .pri insurance scan threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($".pri insurance scan threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 10. M12i Part A2: STALENESS GUARD — see the file-header comment for why this exists. A
//     missing publish folder is a SKIP (not a pass, not a fail), same as checks 8-9.
// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n[10/{Total}] M12i: the publish output is not older than DESKTOP\\Linc.Desktop\\'s newest source file...");
try
{
    var publishDir = Path.Combine(desktopProjectDir, "bin", "x64", "Release", "net8.0-windows10.0.19041.0", "win-x64", "publish");
    if (!Directory.Exists(publishDir))
    {
        Console.WriteLine($"    [10/{Total}] SKIP: no publish output at {publishDir} — run the Beta publish first, then re-run packagesim. Not counted as a pass or a failure.");
        skips.Add("Staleness guard skipped: no publish output present");
    }
    else
    {
        // The publish output's newest-relevant timestamp: the app's OWN rebuilt artifacts, not
        // vendored/content DLLs (scrcpy, VC++ redist, WindowsAppSDK) which keep their original
        // mtime when copied and would make an old publish look falsely fresh.
        var publishArtifacts = new[] { "Linc.Desktop.exe", "Linc.Desktop.dll", "Linc.Desktop.pdb", "Linc.Desktop.pri" }
            .Select(name => Path.Combine(publishDir, name))
            .Where(File.Exists)
            .ToList();

        if (publishArtifacts.Count == 0)
        {
            Check(10, false,
                "found the app's own build artifacts in the publish output.",
                $"none of Linc.Desktop.{{exe,dll,pdb,pri}} exist under {publishDir} — the staleness guard has nothing to compare.",
                "Staleness guard: publish output has none of the app's own build artifacts");
        }
        else
        {
            var publishNewest = publishArtifacts.Max(File.GetLastWriteTimeUtc);

            // DESKTOP\Linc.Desktop\**\*.cs, **\*.xaml and the .csproj itself — excluding bin\,
            // obj\ and Generated Files\, or the guard would compare the build to itself.
            var sourceFiles = Directory.EnumerateFiles(desktopProjectDir, "*.*", SearchOption.AllDirectories)
                .Where(f => (f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                             f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ||
                             f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) &&
                            !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                            !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                            !f.Contains($"{Path.DirectorySeparatorChar}Generated Files{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (sourceFiles.Count == 0)
            {
                Check(10, false,
                    "found source files under DESKTOP\\Linc.Desktop\\ to compare against.",
                    $"found 0 .cs/.xaml/.csproj files under {desktopProjectDir} (excluding bin\\, obj\\, Generated Files\\) — the staleness guard cannot run.",
                    "Staleness guard: no DESKTOP\\Linc.Desktop\\ source files found");
            }
            else
            {
                var newestSource = sourceFiles
                    .Select(f => (File: f, Mtime: File.GetLastWriteTimeUtc(f)))
                    .OrderByDescending(x => x.Mtime)
                    .First();

                // A small grace period so a publish that races a file save (build started a
                // moment before the last edit's mtime landed) is not reported as stale.
                var tolerance = TimeSpan.FromMinutes(2);
                var stale = publishNewest.Add(tolerance) < newestSource.Mtime;

                Check(10, !stale,
                    $"publish ({publishNewest:u}) is not older than the newest source file ({newestSource.Mtime:u}; tolerance {tolerance}).",
                    $"the publish output is STALE: newest publish artifact is {publishNewest:u}, but " +
                    $"{Path.GetRelativePath(repoRoot, newestSource.File)} is newer at {newestSource.Mtime:u} " +
                    $"(tolerance {tolerance}) — re-publish " +
                    "(-t:Publish -p:PublishProfile=Beta -p:Configuration=Release -p:Platform=x64) before shipping this output.",
                    "Publish output is older than DESKTOP\\Linc.Desktop\\'s newest source file (stale publish)");
            }
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    [10/{Total}] FAIL: staleness scan threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Staleness scan threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 11. M14 Part B: CRUDE source-text check that the default palette stayed black-and-white.
//     The M14 palette replaced the M3 purple family with a neutral grey ramp in
//     Themes\MaterialExpressive.xaml (and its Android mirror Color.kt). The old purple literals
//     MUST be absent, and the new neutral ones MUST be present — an absent-guard alone would pass
//     an empty dictionary, a present-guard alone would miss a half-reverted brush. Both directions
//     are pinned. This is the check the M14 negative proof (#1) reverts a brush against.
// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n[11/{Total}] M14: the default palette in MaterialExpressive.xaml is the black-and-white ramp...");
try
{
    var themePath = Path.Combine(desktopProjectDir, "Themes", "MaterialExpressive.xaml");
    if (!File.Exists(themePath))
    {
        Check(11, false, "found Themes\\MaterialExpressive.xaml.", $"could not locate {themePath}; the theme guard cannot run.", "Themes\\MaterialExpressive.xaml not found");
    }
    else
    {
        var theme = File.ReadAllText(themePath);
        string[] oldPurple = ["#6750A4", "#EADDFF", "#4F378B", "#E8DEF8", "#4A4458", "#FFD8E4", "#633B48", "#D0BCFF", "#FEF7FF", "#F3EDF7", "#ECE6F0"];
        var purpleHits = oldPurple.Where(p => theme.Contains(p, StringComparison.OrdinalIgnoreCase)).ToList();

        string[] newNeutral = ["#171717", "#E6E6E6", "#525252", "#1F1F1F", "#131313", "#C9C9C9", "#8E8E8E", "#CC101010"];
        var neutralMissing = newNeutral.Where(n => !theme.Contains(n, StringComparison.OrdinalIgnoreCase)).ToList();

        CheckSub(purpleHits.Count == 0,
            "no M14-era purple-family literals remain (MdPrimary/Md*Container and friends).",
            $"M14-era purple literals found in the default palette: {string.Join(", ", purpleHits)} — the black-and-white ramp was reverted.",
            "Old purple literals present in MaterialExpressive.xaml");
        CheckSub(neutralMissing.Count == 0,
            "the black-and-white ramp literals (light+dark surfaces, text, borders) are present.",
            $"expected black-and-white ramp literals missing: {string.Join(", ", neutralMissing)}.",
            "Black-and-white ramp literals missing from MaterialExpressive.xaml");

        var scrim = theme.Contains("x:Key=\"WallpaperScrimBrush\" Color=\"#CC101010\"", StringComparison.Ordinal);
        CheckSub(scrim,
            "WallpaperScrimBrush is the neutral #CC101010 (was the blue-leaning #CC101014).",
            "WallpaperScrimBrush is not the neutral #CC101010.",
            "WallpaperScrimBrush not neutral");

        Check(11, purpleHits.Count == 0 && neutralMissing.Count == 0 && scrim,
            "the default palette is the black-and-white ramp with the neutral scrim.",
            "the default palette regressed toward the old look (see sub-failures above).",
            "MaterialExpressive.xaml no longer carries the black-and-white ramp");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    [11/{Total}] FAIL: theme scan threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Theme scan threw: {ex.GetType().Name}: {ex.Message}");
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
    // The harness runs from anywhere — `dotnet run` keeps the parent's CWD, which may be the
    // workspace root (yellow\) rather than the Linc\ code root. Walk up, and at each ancestor
    // check both <ancestor>\DESKTOP\... and <ancestor>\Linc/DESKTOP/... so both layouts resolve.
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
    // Fall back to current dir if nothing matched (best effort; the check becomes a WARN).
    return start;
}

/// <summary>
/// M17a A4: the minimum ICO/PNG reading needed to assert the mark's geometry, BCL only.
/// Reads the ICONDIR for the frame sizes, then decodes the 16 px frame's alpha channel far
/// enough to tell a rounded, inset, alpha-bearing mark from a flat opaque square.
/// </summary>
internal readonly record struct IconGeometry(int[] Sizes, int CornerAlpha16, double InkFraction16)
{
    public static IconGeometry Read(string path)
    {
        if (!File.Exists(path))
        {
            return new IconGeometry([], -1, -1);
        }

        var bytes = File.ReadAllBytes(path);
        var count = BitConverter.ToUInt16(bytes, 4);
        var sizes = new List<int>();
        var cornerAlpha = -1;
        var ink = -1.0;

        for (var i = 0; i < count; i++)
        {
            var entry = 6 + (i * 16);
            var width = bytes[entry] == 0 ? 256 : bytes[entry];
            sizes.Add(width);

            if (width != 16)
            {
                continue;
            }

            var length = BitConverter.ToInt32(bytes, entry + 8);
            var offset = BitConverter.ToInt32(bytes, entry + 12);
            var alpha = DecodePngAlpha(bytes.AsSpan(offset, length), 16);
            if (alpha is null)
            {
                continue;
            }

            cornerAlpha = alpha[0];
            ink = alpha.Count(a => a > 128) / 256.0;
        }

        return new IconGeometry([.. sizes], cornerAlpha, ink);
    }

    /// <summary>
    /// Decodes the alpha plane of a non-interlaced 8-bit RGBA PNG of the given square side.
    /// Returns null for any other shape — the caller then leaves its value at -1 and fails loudly
    /// rather than passing on an assumption.
    /// </summary>
    private static byte[]? DecodePngAlpha(ReadOnlySpan<byte> png, int side)
    {
        if (png.Length < 26 || png[0] != 0x89 || png[1] != 0x50)
        {
            return null;
        }

        // IHDR payload starts at byte 16: width, height, bit depth, colour type.
        var width = BinaryPrimitives.ReadInt32BigEndian(png[16..20]);
        var height = BinaryPrimitives.ReadInt32BigEndian(png[20..24]);
        var bitDepth = png[24];
        var colourType = png[25];
        var interlace = png[28];
        if (width != side || height != side || bitDepth != 8 || colourType != 6 || interlace != 0)
        {
            return null; // not 8-bit RGBA, non-interlaced — do not guess
        }

        // Concatenate every IDAT payload, then inflate.
        var idat = new MemoryStream();
        var pos = 8;
        while (pos + 8 <= png.Length)
        {
            var chunkLength = BinaryPrimitives.ReadInt32BigEndian(png[pos..(pos + 4)]);
            var type = System.Text.Encoding.ASCII.GetString(png[(pos + 4)..(pos + 8)]);
            if (type == "IDAT")
            {
                idat.Write(png[(pos + 8)..(pos + 8 + chunkLength)]);
            }
            pos += 12 + chunkLength; // length + type + data + CRC
        }

        idat.Position = 0;
        var raw = new MemoryStream();
        using (var inflate = new ZLibStream(idat, CompressionMode.Decompress))
        {
            inflate.CopyTo(raw);
        }

        var data = raw.ToArray();
        const int bpp = 4;
        var stride = (side * bpp) + 1; // each scanline is prefixed with its filter byte
        if (data.Length < stride * side)
        {
            return null;
        }

        // Un-filter in place into an RGBA buffer (PNG filter types 0-4).
        var image = new byte[side * side * bpp];
        for (var y = 0; y < side; y++)
        {
            var filter = data[y * stride];
            for (var x = 0; x < side * bpp; x++)
            {
                int rawByte = data[(y * stride) + 1 + x];
                var a = x >= bpp ? image[(y * side * bpp) + x - bpp] : 0;
                var b = y > 0 ? image[((y - 1) * side * bpp) + x] : 0;
                var c = x >= bpp && y > 0 ? image[((y - 1) * side * bpp) + x - bpp] : 0;
                var value = filter switch
                {
                    0 => rawByte,
                    1 => rawByte + a,
                    2 => rawByte + b,
                    3 => rawByte + ((a + b) / 2),
                    4 => rawByte + Paeth(a, b, c),
                    _ => rawByte,
                };
                image[(y * side * bpp) + x] = (byte)value;
            }
        }

        var result = new byte[side * side];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = image[(i * bpp) + 3];
        }
        return result;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
