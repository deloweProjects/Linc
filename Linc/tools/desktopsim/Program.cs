using System.Text.Json;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;
using Linc.Desktop.Services;

Console.WriteLine("=== Linc Desktop Mode Verification Harness (desktopsim) ===");

var failures = new List<string>();

// Local helper shared by the [6/7] geometry assertions: enforces the spec's end-to-end
// invariants on a ComputeRecommendedGeometry result — pixel budget, even dims, hard range,
// DPI range, and aspect preservation within tolerance. Each violation records a failure.
void AssertGeometryInvariants(string label, int w, int h, int dpi, double aspectBaseline, double tolerance, List<string> failures)
{
    const long Budget = 1600L * 900L;
    long area = (long)w * h;
    var aspect = (double)w / h;
    var aspectDelta = Math.Abs(aspect - aspectBaseline) / aspectBaseline;

    bool even = (w & 1) == 0 && (h & 1) == 0;
    bool inRange = w is >= 640 and <= 3840 && h is >= 640 and <= 3840;
    bool withinBudget = area <= Budget;
    bool dpiOk = dpi is >= 120 and <= 320;
    bool aspectOk = aspectDelta <= tolerance;

    if (even && inRange && withinBudget && dpiOk && aspectOk)
    {
        Console.WriteLine($"    PASS (invariants): {label} -> {w}x{h}/{dpi} area={area} aspect={aspect:0.####} (baseline {aspectBaseline:0.####}, Δ {aspectDelta * 100:0.##}%)");
        return;
    }
    if (!even) failures.Add($"{label}: dims not even ({w}x{h})");
    if (!inRange) failures.Add($"{label}: dims out of 640–3840 ({w}x{h})");
    if (!withinBudget) failures.Add($"{label}: area {area} exceeds budget {Budget} ({w}x{h})");
    if (!dpiOk) failures.Add($"{label}: dpi {dpi} out of 120–320");
    if (!aspectOk) failures.Add($"{label}: aspect {aspect:0.####} deviates {aspectDelta * 100:0.##}% from baseline {aspectBaseline:0.####} (tol {tolerance * 100:0.##}%)");
    Console.WriteLine($"    FAIL (invariants): {label} -> {w}x{h}/{dpi} area={area} aspect={aspect:0.####} (baseline {aspectBaseline:0.####}, Δ {aspectDelta * 100:0.##}%)");
}

// 1. Round-trip DesktopModeSettings through DeviceRegistry in a throwaway temp root (D-057):
// the registry cannot reach %LOCALAPPDATA%\Linc, so there is nothing to back up or restore.
Console.WriteLine("\n[1/7] Round-tripping DesktopModeSettings through DeviceRegistry...");
var tempDir = Path.Combine(Path.GetTempPath(), "Linc_desktopsim_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
var tempSettingsPath = Path.Combine(tempDir, "settings.json");

try
{
    var reg = new DeviceRegistry(tempDir);
    reg.SavePairedDevice("TEST-SERIAL-1", "Pixel 7");

    var nonDefaultSettings = new DesktopModeSettings(
        VirtualDisplayWidth: 2560,
        VirtualDisplayHeight: 1440,
        VirtualDisplayDpi: 320,
        AspectRatioMode: AspectRatioMode.Custom,
        DefaultWindowMode: DefaultWindowMode.Maximized,
        ResizableWindows: false,
        AutoFullscreenApps: true,
        SetupCompleted: true,
        RebootPending: false,
        CaptureMouse: false,
        MaxFps: 60,
        VideoBitRate: "12M",
        ForwardAudio: true,
        LaunchAppsFreeform: false);

    reg.SaveDesktopMode(nonDefaultSettings);

    var reloadedReg = new DeviceRegistry(tempDir);
    var loadedSettings = reloadedReg.DesktopMode;

    if (loadedSettings == nonDefaultSettings)
    {
        Console.WriteLine("    PASS: DesktopModeSettings round-tripped identically through DeviceRegistry.");
    }
    else
    {
        Console.WriteLine($"    FAIL: Settings mismatch after reload.\n      Expected: {nonDefaultSettings}\n      Actual:   {loadedSettings}");
        failures.Add("DesktopModeSettings round-trip mismatch");
    }

    // 2. Backward-compatibility check: KnownDevice JSON blob WITHOUT DesktopMode field
    Console.WriteLine("\n[2/7] Checking backward compatibility (JSON without DesktopMode field)...");

    var legacyJson = """
    {
      "PairedSerial": "LEGACY-SERIAL",
      "PairedModel": "Pixel 6",
      "KnownDevices": [
        {
          "Serial": "LEGACY-SERIAL",
          "Model": "Pixel 6",
          "FirstPairedUtc": "2026-01-01T00:00:00+00:00",
          "LastHostPort": "192.168.1.100:5555"
        }
      ]
    }
    """;

    File.WriteAllText(tempSettingsPath, legacyJson);

    var legacyReg = new DeviceRegistry(tempDir);
    var legacySettings = legacyReg.DesktopMode;
    var defaultSettings = new DesktopModeSettings();

    if (legacySettings == defaultSettings)
    {
        Console.WriteLine("    PASS: Legacy KnownDevice JSON without DesktopMode deserialized with default settings.");
    }
    else
    {
        Console.WriteLine($"    FAIL: Legacy KnownDevice JSON did not yield default settings.\n      Actual: {legacySettings}");
        failures.Add("Backward-compat JSON check failed");
    }
}
finally
{
    // A killed run leaks this folder instead of damaging the owner's pairing (D-057).
    try { Directory.Delete(tempDir, recursive: true); } catch { }
}

// 3. Call IsSetupAppliedAsync against the attached Pixel 7 and print the four on-device values
Console.WriteLine("\n[3/7] Querying attached device ADB desktop mode settings (read-only)...");
var logTempDir = Path.Combine(Path.GetTempPath(), "Linc_desktopsim_log_" + Guid.NewGuid().ToString("N"));
try
{
    var adbHost = new AdbServerHost();
    // A0: LogService now takes its root from IDeviceRegistry.RootPath (D-057/D-058) — a throwaway
    // registry here, same as [1/7], so this harness never touches the owner's real logs\.
    var log = new LogService(new DeviceRegistry(logTempDir));
    var desktopService = new DesktopModeService(adbHost, log);

    await adbHost.EnsureRunningAsync(CancellationToken.None);
    var adb = new AdbClient();
    var devices = await adb.GetDevicesAsync(CancellationToken.None);

    var device = devices.FirstOrDefault(d => d.State == DeviceState.Online);
    if (device is null)
    {
        Console.WriteLine("    WARN: No online ADB device attached.");
        failures.Add("No attached ADB device found");
    }
    else
    {
        Console.WriteLine($"    Found attached device: {device.Serial} ({device.Model})");

        var receiver = new ConsoleOutputReceiver();
        
        async Task<string> GetSetting(string ns, string key)
        {
            var r = new ConsoleOutputReceiver();
            await adb.ExecuteRemoteCommandAsync($"settings get {ns} {key}", device, r, CancellationToken.None);
            return r.ToString().Trim();
        }

        var v1 = await GetSetting("global", "force_resizable_activities");
        var v2 = await GetSetting("global", "enable_freeform_support");
        var v3 = await GetSetting("global", "force_desktop_mode_on_external_displays");
        var v4 = await GetSetting("secure", "desktop_mode");

        Console.WriteLine($"      global force_resizable_activities:          '{v1}'");
        Console.WriteLine($"      global enable_freeform_support:             '{v2}'");
        Console.WriteLine($"      global force_desktop_mode_on_external_displays: '{v3}'");
        Console.WriteLine($"      secure desktop_mode:                        '{v4}'");

        var isApplied = await desktopService.IsSetupAppliedAsync(device, CancellationToken.None);
        Console.WriteLine($"    IsSetupAppliedAsync result: {isApplied}");
        Console.WriteLine("    PASS: Successfully queried device settings read-only.");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: Querying device settings failed: {ex.Message}");
    failures.Add($"Device query failed: {ex.Message}");
}
finally
{
    try { Directory.Delete(logTempDir, recursive: true); } catch { }
}

// 4. Verify DesktopLaunchService scrcpy argument list construction
Console.WriteLine("\n[4/7] Verifying DesktopLaunchService scrcpy argument list construction...");
try
{
    // Test 4a: Default settings produce fallback 1600x900/200, uhid mouse, 30 fps, 8M bitrate, no audio
    var zeroedSettings = new DesktopModeSettings();
    var zeroedArgs = DesktopLaunchService.BuildScrcpyArgs("TEST-SERIAL-1", zeroedSettings, getScreenResolution: () => (1920, 1080));
    var expectedZeroed = new List<string>
    {
        "-s", "TEST-SERIAL-1",
        "--new-display=1600x900/200",
        "--mouse=uhid",
        "--keyboard=uhid",
        "--window-title", "Linc Desktop",
        "--stay-awake",
        "--max-fps=30",
        "--video-bit-rate=8M",
        "--no-audio"
    };

    if (zeroedArgs.SequenceEqual(expectedZeroed))
    {
        Console.WriteLine($"    PASS: Default settings correctly produced expected args: {string.Join(" ", zeroedArgs)}");
    }
    else
    {
        Console.WriteLine($"    FAIL: Default settings args mismatch.\n      Expected: {string.Join(" ", expectedZeroed)}\n      Actual:   {string.Join(" ", zeroedArgs)}");
        failures.Add("Default settings scrcpy argument list mismatch");
    }

    // Test 4b: CaptureMouse = false produces --mouse=sdk and NOT --mouse=uhid
    var sdkMouseSettings = new DesktopModeSettings(CaptureMouse: false);
    var sdkMouseArgs = DesktopLaunchService.BuildScrcpyArgs("TEST-SERIAL-1", sdkMouseSettings, getScreenResolution: () => (1920, 1080));
    if (sdkMouseArgs.Contains("--mouse=sdk") && !sdkMouseArgs.Contains("--mouse=uhid"))
    {
        Console.WriteLine("    PASS: CaptureMouse = false correctly produced --mouse=sdk and omitted --mouse=uhid.");
    }
    else
    {
        Console.WriteLine($"    FAIL: CaptureMouse = false args mismatch. Actual: {string.Join(" ", sdkMouseArgs)}");
        failures.Add("CaptureMouse = false scrcpy argument mismatch");
    }

    // Test 4c: ForwardAudio = true produces NO --no-audio
    var forwardAudioSettings = new DesktopModeSettings(ForwardAudio: true);
    var forwardAudioArgs = DesktopLaunchService.BuildScrcpyArgs("TEST-SERIAL-1", forwardAudioSettings, getScreenResolution: () => (1920, 1080));
    if (!forwardAudioArgs.Contains("--no-audio"))
    {
        Console.WriteLine("    PASS: ForwardAudio = true correctly omitted --no-audio.");
    }
    else
    {
        Console.WriteLine($"    FAIL: ForwardAudio = true args included --no-audio. Actual: {string.Join(" ", forwardAudioArgs)}");
        failures.Add("ForwardAudio = true scrcpy argument mismatch");
    }

    // Test 4d: Explicit non-zero settings passed verbatim (1600x900/220 with ForwardAudio = true)
    var explicitSettings = new DesktopModeSettings(VirtualDisplayWidth: 1600, VirtualDisplayHeight: 900, VirtualDisplayDpi: 220, ForwardAudio: true);
    var explicitArgs = DesktopLaunchService.BuildScrcpyArgs("TEST-SERIAL-1", explicitSettings);
    var expectedExplicit = new List<string>
    {
        "-s", "TEST-SERIAL-1",
        "--new-display=1600x900/220",
        "--mouse=uhid",
        "--keyboard=uhid",
        "--window-title", "Linc Desktop",
        "--stay-awake",
        "--max-fps=30",
        "--video-bit-rate=8M"
    };

    if (explicitArgs.SequenceEqual(expectedExplicit))
    {
        Console.WriteLine($"    PASS: Explicit settings (1600x900/220, ForwardAudio=true) correctly passed verbatim: {string.Join(" ", explicitArgs)}");
    }
    else
    {
        Console.WriteLine($"    FAIL: Explicit settings args mismatch.\n      Expected: {string.Join(" ", expectedExplicit)}\n      Actual:   {string.Join(" ", explicitArgs)}");
        failures.Add("Explicit settings scrcpy argument list mismatch");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: Argument construction check failed: {ex.Message}");
    failures.Add($"Argument construction check failed: {ex.Message}");
}

// 5. Validate the pure DesktopModeSettings.ValidateSettingsFields helper
Console.WriteLine("\n[5/7] Validating DesktopModeSettings.ValidateSettingsFields (range + bit-rate)...");
try
{
    // 5a: Out-of-range width rejected
    var widthTooHigh = DesktopModeSettings.ValidateSettingsFields(5000, 900, 200, 30, "8M");
    if (widthTooHigh is not null)
    {
        Console.WriteLine($"    PASS: Out-of-range width (5000) rejected: \"{widthTooHigh}\"");
    }
    else
    {
        Console.WriteLine("    FAIL: Out-of-range width (5000) was NOT rejected.");
        failures.Add("Validation: out-of-range width not rejected");
    }

    // 5b: Out-of-range height rejected
    var heightTooLow = DesktopModeSettings.ValidateSettingsFields(1280, 100, 200, 30, "8M");
    if (heightTooLow is not null)
    {
        Console.WriteLine($"    PASS: Out-of-range height (100) rejected: \"{heightTooLow}\"");
    }
    else
    {
        Console.WriteLine("    FAIL: Out-of-range height (100) was NOT rejected.");
        failures.Add("Validation: out-of-range height not rejected");
    }

    // 5c: Out-of-range DPI rejected
    var dpiTooHigh = DesktopModeSettings.ValidateSettingsFields(1280, 720, 500, 30, "8M");
    if (dpiTooHigh is not null)
    {
        Console.WriteLine($"    PASS: Out-of-range DPI (500) rejected: \"{dpiTooHigh}\"");
    }
    else
    {
        Console.WriteLine("    FAIL: Out-of-range DPI (500) was NOT rejected.");
        failures.Add("Validation: out-of-range DPI not rejected");
    }

    // 5d: Out-of-range MaxFps rejected
    var fpsTooHigh = DesktopModeSettings.ValidateSettingsFields(1280, 720, 200, 0, "8M");
    if (fpsTooHigh is not null)
    {
        Console.WriteLine($"    PASS: Out-of-range MaxFps (0) rejected: \"{fpsTooHigh}\"");
    }
    else
    {
        Console.WriteLine("    FAIL: Out-of-range MaxFps (0) was NOT rejected.");
        failures.Add("Validation: out-of-range MaxFps not rejected");
    }

    // 5e: Malformed bit rate rejected
    var badBitRate = DesktopModeSettings.ValidateSettingsFields(1280, 720, 200, 30, "abc");
    if (badBitRate is not null)
    {
        Console.WriteLine($"    PASS: Malformed bit rate (\"abc\") rejected: \"{badBitRate}\"");
    }
    else
    {
        Console.WriteLine("    FAIL: Malformed bit rate (\"abc\") was NOT rejected.");
        failures.Add("Validation: malformed bit rate not rejected");
    }

    // 5f: Valid values accepted (returns null)
    var validAllZero = DesktopModeSettings.ValidateSettingsFields(0, 0, 0, 30, "8M");
    var validExplicit = DesktopModeSettings.ValidateSettingsFields(1600, 900, 200, 60, "12K");
    if (validAllZero is null && validExplicit is null)
    {
        Console.WriteLine("    PASS: Valid settings (all-zero geometry + explicit 1600x900/200/60fps/12K) accepted.");
    }
    else
    {
        if (validAllZero is not null) failures.Add($"Validation: all-zero was wrongly rejected (\"{validAllZero}\")");
        if (validExplicit is not null) failures.Add($"Validation: explicit valid was wrongly rejected (\"{validExplicit}\")");
        Console.WriteLine("    FAIL: A valid settings set was rejected when it should have been accepted.");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: Validation check threw: {ex.Message}");
    failures.Add($"Validation check threw: {ex.Message}");
}

// 6. ParseWmOutput + ComputeRecommendedGeometry (pure, no phone)
Console.WriteLine("\n[6/7] Testing ComputeRecommendedGeometry + ParseWmOutput (pure, representative Pixel 7 output)...");
try
{
    // Representative output from the attached Pixel 7.
    var sizeOutput = "Physical size: 1080x2400";
    var densityOutput = "Physical density: 420";

    var (phoneW, phoneH, phoneDpi, parsed) = DesktopModeService.ParseWmOutput(sizeOutput, densityOutput);
    if (parsed && phoneW == 1080 && phoneH == 2400 && phoneDpi == 420)
    {
        Console.WriteLine($"    PASS: ParseWmOutput parsed '{sizeOutput}' / '{densityOutput}' -> {phoneW}x{phoneH}/{phoneDpi}");
    }
    else
    {
        Console.WriteLine($"    FAIL: ParseWmOutput expected 1080x2400/420, got {phoneW}x{phoneH}/{phoneDpi} (parsed={parsed})");
        failures.Add("ParseWmOutput did not parse representative Pixel 7 output correctly");
    }

    // 6b: ComputeRecommendedGeometry in MatchMonitor mode with a 1920x1080 monitor must land at
    // 1600x900 exactly (aspect preserved to the monitor's 16:9, area == the 1,440,000 px budget).
    const int ExpectedMmW = 1600, ExpectedMmH = 900, ExpectedMmDpi = 160;
    var monitorSettings = new DesktopModeSettings(AspectRatioMode: AspectRatioMode.MatchMonitor);
    var (mmW, mmH, mmDpi) = DesktopModeService.ComputeRecommendedGeometry(
        monitorSettings, phoneW, phoneH, phoneDpi,
        getMonitorResolution: () => (1920, 1080));
    if (mmW == ExpectedMmW && mmH == ExpectedMmH && mmDpi == ExpectedMmDpi)
    {
        Console.WriteLine($"    PASS: MatchMonitor (monitor 1920x1080) -> {mmW}x{mmH}/{mmDpi} (exactly 1600x900/160, area={mmW * mmH})");
    }
    else
    {
        Console.WriteLine($"    FAIL: MatchMonitor expected {ExpectedMmW}x{ExpectedMmH}/{ExpectedMmDpi}, got {mmW}x{mmH}/{mmDpi}");
        failures.Add($"ComputeRecommendedGeometry MatchMonitor = {mmW}x{mmH}/{mmDpi}, expected {ExpectedMmW}x{ExpectedMmH}/{ExpectedMmDpi}");
    }

    // 6b-assert: budget, even, range, aspect (16:9 monitor).
    AssertGeometryInvariants("MatchMonitor", mmW, mmH, mmDpi, aspectBaseline: 1920.0 / 1080.0, tolerance: 0.01, failures);

    // 6c: MatchPhone mode on a 1080x2400 phone must preserve the phone's portrait aspect (≈0.45)
    // within ±2%, never the raw 1:1 resolution, area ≤ 1,440,000, both dims even and in 640–3840.
    var phoneSettings = new DesktopModeSettings(AspectRatioMode: AspectRatioMode.MatchPhone);
    var (mpW, mpH, mpDpi) = DesktopModeService.ComputeRecommendedGeometry(
        phoneSettings, phoneW, phoneH, phoneDpi, getMonitorResolution: () => (1920, 1080));
    var oneToOne = mpW == phoneW && mpH == phoneH;
    // Expected ~804x1788 (aspect-preserved pixel-budget result for 1080x2400).
    if (oneToOne)
    {
        Console.WriteLine($"    FAIL: MatchPhone returned raw phone resolution 1:1 ({mpW}x{mpH})");
        failures.Add("ComputeRecommendedGeometry MatchPhone returned raw phone resolution 1:1");
    }
    else
    {
        Console.WriteLine($"    PASS: MatchPhone -> {mpW}x{mpH}/{mpDpi} (not 1:1; computed from phone aspect)");
    }
    AssertGeometryInvariants("MatchPhone", mpW, mpH, mpDpi, aspectBaseline: (double)phoneW / phoneH, tolerance: 0.02, failures);
    if (oneToOne) failures.Add("MatchPhone returned 1:1 (already recorded above)");

    // 6d: Custom mode leaves geometry untouched
    var customSettings = new DesktopModeSettings(
        VirtualDisplayWidth: 1000, VirtualDisplayHeight: 800, VirtualDisplayDpi: 240,
        AspectRatioMode: AspectRatioMode.Custom);
    var (cW, cH, cDpi) = DesktopModeService.ComputeRecommendedGeometry(
        customSettings, phoneW, phoneH, phoneDpi, getMonitorResolution: () => (1920, 1080));
    if (cW == 1000 && cH == 800 && cDpi == 240)
    {
        Console.WriteLine($"    PASS: Custom mode -> {cW}x{cH}/{cDpi} (geometry left untouched)");
    }
    else
    {
        Console.WriteLine($"    FAIL: Custom mode changed geometry: expected 1000x800/240, got {cW}x{cH}/{cDpi}");
        failures.Add($"ComputeRecommendedGeometry Custom mode changed geometry from input: {cW}x{cH}/{cDpi}");
    }

    // 6e: ParseWmOutput returns false/unparsed on garbage
    var (_, _, _, garbParsed) = DesktopModeService.ParseWmOutput("garbage", "nope");
    if (!garbParsed)
    {
        Console.WriteLine("    PASS: ParseWmOutput returned false on garbage input");
    }
    else
    {
        Console.WriteLine("    FAIL: ParseWmOutput parsed garbage as valid");
        failures.Add("ParseWmOutput parsed garbage input");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: ComputeRecommendedGeometry check threw: {ex.Message}");
    failures.Add($"ComputeRecommendedGeometry check threw: {ex.Message}");
}

// 7. Summary-level sanity: a KnownDevice loaded from legacy JSON carries default DesktopMode
// (this was check #2 inside the registry block; here we just re-affirm the persisted blob shape)
Console.WriteLine("\n[7/7] Re-affirming legacy Backward-compat (default-vs-explicit on a fresh registry)...");
try
{
    var legacyOnly = """
    {
      "PairedSerial": "LEGACY-2",
      "PairedModel": "Phone X",
      "KnownDevices": [
        {
          "Serial": "LEGACY-2",
          "Model": "Phone X",
          "FirstPairedUtc": "2026-01-01T00:00:00+00:00"
        }
      ]
    }
    """;
    // Its own throwaway root (D-057); the [1/7] one is long deleted by now.
    var tempDir2 = Path.Combine(Path.GetTempPath(), "Linc_desktopsim_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDir2);
    try
    {
        File.WriteAllText(Path.Combine(tempDir2, "settings.json"), legacyOnly);

        var reg = new DeviceRegistry(tempDir2);
        var def = new DesktopModeSettings();
        // DesktopMode defaults should NOT be a fully-explicit profile accidentally persisted;
        // the record defaults (all-false/zero) signal "compute a fallback on launch."
        if (reg.DesktopMode == def && reg.PairedSerial == "LEGACY-2")
        {
            Console.WriteLine("    PASS: Legacy JSON without DesktopMode loaded with record defaults (no fallback recomputed).");
        }
        else
        {
            Console.WriteLine($"    FAIL: Legacy JSON DesktopMode = {reg.DesktopMode}; Paired = {reg.PairedSerial}");
            failures.Add("Legacy JSON DesktopMode did not yield default DesktopModeSettings");
        }
    }
    finally
    {
        try { Directory.Delete(tempDir2, recursive: true); } catch (IOException) { }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: Re-affirming legacy backward-compat threw: {ex.Message}");
    failures.Add($"Re-affirming legacy backward-compat threw: {ex.Message}");
}

// 8. Print clear PASS/FAIL summary and exit code
Console.WriteLine("\n=== SUMMARY ===");
if (failures.Count == 0)
{
    Console.WriteLine("PASS: All verification checks succeeded.");
    return 0;
}
else
{
    Console.WriteLine($"FAIL: {failures.Count} check(s) failed:");
    foreach (var f in failures)
    {
        Console.WriteLine($"  - {f}");
    }
    return 1;
}
