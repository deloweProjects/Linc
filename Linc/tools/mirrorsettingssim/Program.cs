using Linc.Desktop.Services;

Console.WriteLine("=== Linc Mirror Settings Verification Harness (mirrorsettingssim) ===");

var failures = new List<string>();

// 1. Round-trip MirrorSettings through DeviceRegistry in a throwaway temp root (D-057): the
// registry cannot reach %LOCALAPPDATA%\Linc at all, so there is nothing to back up or restore
// and no ritual for a future section to forget.
Console.WriteLine("\n[1/8] Round-tripping MirrorSettings through DeviceRegistry...");
var root = Path.Combine(Path.GetTempPath(), "Linc_mirrorsettingssim_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var settingsPath = Path.Combine(root, "settings.json");

try
{
    var reg = new DeviceRegistry(root);
    reg.SavePairedDevice("TEST-SERIAL-M1", "Pixel 7");

    // A never-touched device's Mirror persists as null; the registry getter substitutes
    // the Balanced-equivalent defaults so a brand-new user's args are byte-identical to
    // the pre-M5b Balanced preset.
    var nonDefault = new MirrorSettings(
        MaxSize: 1600,
        VideoBitRate: "12M",
        MaxFps: 60,
        Crop: "1080x1920:0:0",
        StayAwake: false,
        TurnScreenOff: true,
        ShowTouches: true);

    reg.SaveMirror(nonDefault);

    var reloaded = new DeviceRegistry(root);
    var loadedMirror = reloaded.Mirror;

    if (loadedMirror == nonDefault)
    {
        Console.WriteLine("    PASS: MirrorSettings round-tripped identically through DeviceRegistry.");
    }
    else
    {
        Console.WriteLine($"    FAIL: Settings mismatch after reload.\n      Expected: {nonDefault}\n      Actual:   {loadedMirror}");
        failures.Add("MirrorSettings round-trip mismatch");
    }

    // 1b: a device whose Mirror field is still null must yield BalancedDefaults (M5b
    // byte-identity guarantee — see DeviceRegistry.Mirror getter). Save a new paired
    // device whose record has no Mirror field, then read it back without ever saving
    // a MirrorSettings of our own.
    reg.SavePairedDevice("NEVER-TOUCHED", "Pixel 7a");
    reg.SetActiveDevice("NEVER-TOUCHED");
    var untouchedMirror = reg.Mirror;
    if (untouchedMirror == MirrorSettings.BalancedDefaults)
    {
        Console.WriteLine($"    PASS: Never-touched device's Mirror equals BalancedDefaults: {untouchedMirror}");
    }
    else
    {
        Console.WriteLine($"    FAIL: Never-touched Mirror != BalancedDefaults.\n      Expected: {MirrorSettings.BalancedDefaults}\n      Actual:   {untouchedMirror}");
        failures.Add("Never-touched device's Mirror is not BalancedDefaults");
    }

    // 2. Backward-compatibility: a legacy KnownDevice JSON WITHOUT a Mirror field
    // must deserialize to record defaults (so old settings.json files keep working).
    Console.WriteLine("\n[2/8] Checking backward compatibility (JSON without Mirror field)...");
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
    File.WriteAllText(settingsPath, legacyJson);

    var legacyReg = new DeviceRegistry(root);
    var legacyMirror = legacyReg.Mirror;

    // No Mirror field in the JSON → the registry getter substitutes BalancedDefaults
    // (M5b byte-identity guarantee: a never-touched device launches identical to today's
    // Balanced preset).
    if (legacyMirror == MirrorSettings.BalancedDefaults)
    {
        Console.WriteLine("    PASS: Legacy KnownDevice JSON without Mirror deserialized with BalancedDefaults.");
    }
    else
    {
        Console.WriteLine($"    FAIL: Legacy JSON did not yield BalancedDefaults.\n      Expected: {MirrorSettings.BalancedDefaults}\n      Actual:   {legacyMirror}");
        failures.Add("Backward-compat JSON check failed");
    }
}
finally
{
    // A killed run leaks this folder instead of damaging the owner's pairing (D-057).
    try { Directory.Delete(root, recursive: true); } catch (IOException) { }
}

// 3. ValidateSettingsFields: accept valid input, reject each invalid case.
Console.WriteLine("\n[3/8] Validating MirrorSettings.ValidateSettingsFields (ranges + bit-rate + crop)...");
try
{
    // 3a: out-of-range MaxSize (too small) rejected
    var sizeTooSmall = MirrorSettings.ValidateSettingsFields(100, "8M", 0, null);
    if (sizeTooSmall is not null)
    {
        Console.WriteLine($"    PASS: Out-of-range MaxSize (100) rejected: \"{sizeTooSmall}\"");
    }
    else
    {
        Console.WriteLine("    FAIL: Out-of-range MaxSize (100) was NOT rejected.");
        failures.Add("Validation: out-of-range MaxSize (low) not rejected");
    }

    // 3b: out-of-range MaxSize (too large) rejected
    var sizeTooLarge = MirrorSettings.ValidateSettingsFields(5000, "8M", 0, null);
    if (sizeTooLarge is not null)
    {
        Console.WriteLine($"    PASS: Out-of-range MaxSize (5000) rejected: \"{sizeTooLarge}\"");
    }
    else
    {
        Console.WriteLine("    FAIL: Out-of-range MaxSize (5000) was NOT rejected.");
        failures.Add("Validation: out-of-range MaxSize (high) not rejected");
    }

    // 3c: MaxSize of 0 (the "native" sentry) is ACCEPTED
    var sizeZero = MirrorSettings.ValidateSettingsFields(0, "8M", 0, null);
    if (sizeZero is null)
    {
        Console.WriteLine("    PASS: MaxSize 0 (use native) accepted.");
    }
    else
    {
        Console.WriteLine($"    FAIL: MaxSize 0 was wrongly rejected: \"{sizeZero}\"");
        failures.Add("Validation: MaxSize 0 wrongly rejected");
    }

    // 3d: malformed bit rate rejected (the Desktop Mode ^\d+[KM]$ pattern)
    var badBitRate = MirrorSettings.ValidateSettingsFields(1280, "abc", 0, null);
    if (badBitRate is not null)
    {
        Console.WriteLine($"    PASS: Malformed bit rate (\"abc\") rejected: \"{badBitRate}\"");
    }
    else
    {
        Console.WriteLine("    FAIL: Malformed bit rate (\"abc\") was NOT rejected.");
        failures.Add("Validation: malformed bit rate not rejected");
    }

    // 3e: bit rate without a unit rejected
    var bareNumber = MirrorSettings.ValidateSettingsFields(1280, "8", 0, null);
    if (bareNumber is not null)
    {
        Console.WriteLine($"    PASS: Bare-number bit rate (\"8\") rejected: \"{bareNumber}\"");
    }
    else
    {
        Console.WriteLine("    FAIL: Bare-number bit rate (\"8\") was NOT rejected.");
        failures.Add("Validation: bare-number bit rate not rejected");
    }

    // 3f: out-of-range MaxFps rejected (too high)
    var fpsTooHigh = MirrorSettings.ValidateSettingsFields(1280, "8M", 200, null);
    if (fpsTooHigh is not null)
    {
        Console.WriteLine($"    PASS: Out-of-range MaxFps (200) rejected: \"{fpsTooHigh}\"");
    }
    else
    {
        Console.WriteLine("    FAIL: Out-of-range MaxFps (200) was NOT rejected.");
        failures.Add("Validation: out-of-range MaxFps not rejected");
    }

    // 3g: MaxFps 0 is ACCEPTED (the "scrcpy default" sentry)
    var fpsZero = MirrorSettings.ValidateSettingsFields(1280, "8M", 0, null);
    if (fpsZero is null)
    {
        Console.WriteLine("    PASS: MaxFps 0 (scrcpy default) accepted.");
    }
    else
    {
        Console.WriteLine($"    FAIL: MaxFps 0 was wrongly rejected: \"{fpsZero}\"");
        failures.Add("Validation: MaxFps 0 wrongly rejected");
    }

    // 3h: malformed crop rejected (only 3 numbers, not 4)
    var badCrop = MirrorSettings.ValidateSettingsFields(1280, "8M", 0, "1280x720:0");
    if (badCrop is not null)
    {
        Console.WriteLine($"    PASS: Malformed crop (\"1280x720:0\") rejected: \"{badCrop}\"");
    }
    else
    {
        Console.WriteLine("    FAIL: Malformed crop (\"1280x720:0\") was NOT rejected.");
        failures.Add("Validation: malformed crop not rejected");
    }

    // 3i: negative crop component rejected
    var negCrop = MirrorSettings.ValidateSettingsFields(1280, "8M", 0, "1280x720:-1:0");
    if (negCrop is not null)
    {
        Console.WriteLine($"    PASS: Negative crop component (\"1280x720:-1:0\") rejected: \"{negCrop}\"");
    }
    else
    {
        Console.WriteLine("    FAIL: Negative crop component was NOT rejected.");
        failures.Add("Validation: negative crop component not rejected");
    }

    // 3j: well-formed crop accepted
    var goodCrop = MirrorSettings.ValidateSettingsFields(1280, "8M", 0, "1080x1920:0:0");
    if (goodCrop is null)
    {
        Console.WriteLine("    PASS: Well-formed crop (\"1080x1920:0:0\") accepted.");
    }
    else
    {
        Console.WriteLine($"    FAIL: Well-formed crop was wrongly rejected: \"{goodCrop}\"");
        failures.Add("Validation: well-formed crop wrongly rejected");
    }

    // 3k: a fully valid set accepted
    var validAll = MirrorSettings.ValidateSettingsFields(0, "8M", 0, null);
    var validExplicit = MirrorSettings.ValidateSettingsFields(1280, "4K", 30, "400x800:0:0");
    if (validAll is null && validExplicit is null)
    {
        Console.WriteLine("    PASS: Valid sets (defaults + explicit 1280/4K/30fps/crop) accepted.");
    }
    else
    {
        if (validAll is not null) failures.Add($"Validation: defaults wrongly rejected (\"{validAll}\")");
        if (validExplicit is not null) failures.Add($"Validation: explicit valid wrongly rejected (\"{validExplicit}\")");
        Console.WriteLine("    FAIL: A valid settings set was rejected when it should have been accepted.");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: Validation check threw: {ex.Message}");
    failures.Add($"Validation check threw: {ex.Message}");
}

// 4. BuildScrcpyArgs from the never-touched baseline (BalancedDefaults) must reproduce the
// historical "Balanced" preset byte-for-byte — the documented M5b byte-identity guarantee.
Console.WriteLine("\n[4/8] Verifying BuildScrcpyArgs from BalancedDefaults reproduces today's Balanced args...");
try
{
    var built = MirrorService.BuildScrcpyArgs("PIXEL7", MirrorSettings.BalancedDefaults);
    var expected = new List<string>
    {
        "-s", "PIXEL7",
        "-b", "8M",
        "-m", "1280",
        "--window-title", "Linc",
        "--window-borderless",
        "--stay-awake"
    };

    if (built.SequenceEqual(expected))
    {
        Console.WriteLine($"    PASS: BalancedDefaults args equal the historical Balanced args: {string.Join(" ", built)}");
    }
    else
    {
        Console.WriteLine($"    FAIL: BalancedDefaults args mismatch.\n      Expected: {string.Join(" ", expected)}\n      Actual:   {string.Join(" ", built)}");
        failures.Add("BalancedDefaults scrcpy args do not match historical Balanced");
    }

    // 4b: pure record default (new MirrorSettings(), MaxSize=0) omits -m ("0 = native").
    var bareBuilt = MirrorService.BuildScrcpyArgs("PIXEL7", new MirrorSettings());
    if (!bareBuilt.Contains("-m") && !bareBuilt.Any(a => a.StartsWith("-m=")))
    {
        Console.WriteLine($"    PASS: Record default (MaxSize 0 = native) omits -m: {string.Join(" ", bareBuilt)}");
    }
    else
    {
        Console.WriteLine($"    FAIL: Record default wrongly emitted -m: {string.Join(" ", bareBuilt)}");
        failures.Add("Record default (MaxSize 0) wrongly emitted -m");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: Default args check threw: {ex.Message}");
    failures.Add($"Default args check threw: {ex.Message}");
}

// 5. BuildScrcpyArgs from fully-populated settings emits every flag in the right order.
Console.WriteLine("\n[5/8] Verifying BuildScrcpyArgs from fully-populated settings...");
try
{
    var full = new MirrorSettings(
        MaxSize: 1600,
        VideoBitRate: "12M",
        MaxFps: 60,
        Crop: "1080x1920:0:0",
        StayAwake: true,
        TurnScreenOff: true,
        ShowTouches: true);
    var built = MirrorService.BuildScrcpyArgs("PIXEL7", full);
    var expected = new List<string>
    {
        "-s", "PIXEL7",
        "-b", "12M",
        "-m", "1600",
        "--window-title", "Linc",
        "--window-borderless",
        "--max-fps=60",
        "--crop=1080x1920:0:0",
        "--stay-awake",
        "--turn-screen-off",
        "--show-touches"
    };

    if (built.SequenceEqual(expected))
    {
        Console.WriteLine($"    PASS: Fully-populated args as expected: {string.Join(" ", built)}");
    }
    else
    {
        Console.WriteLine($"    FAIL: Fully-populated args mismatch.\n      Expected: {string.Join(" ", expected)}\n      Actual:   {string.Join(" ", built)}");
        failures.Add("Fully-populated MirrorSettings scrcpy args mismatch");
    }

    // 5b: StayAwake=false must drop --stay-awake; TurnScreenOff/ShowTouches omitted when false.
    var noFlags = new MirrorSettings(StayAwake: false);
    var noFlagsBuilt = MirrorService.BuildScrcpyArgs("PIXEL7", noFlags);
    if (!noFlagsBuilt.Contains("--stay-awake")
        && !noFlagsBuilt.Contains("--turn-screen-off")
        && !noFlagsBuilt.Contains("--show-touches"))
    {
        Console.WriteLine("    PASS: StayAwake=false correctly omitted --stay-awake (and the other flags stayed off).");
    }
    else
    {
        Console.WriteLine($"    FAIL: Flag-omission check. Args: {string.Join(" ", noFlagsBuilt)}");
        failures.Add("BuildScrcpyArgs did not omit disabled flags");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: Fully-populated args check threw: {ex.Message}");
    failures.Add($"Fully-populated args check threw: {ex.Message}");
}

// 6. M11 A2.3 — THE BYTE-IDENTITY RULE for audio. A device whose settings have never been
// touched (AudioEnabled=true, the other two audio fields at their "use scrcpy default" values)
// must produce a scrcpy command line with NO audio flag at all. Only AudioEnabled=false adds
// --no-audio; only a non-default bit rate or source adds its own flag, and only while audio
// stays enabled.
Console.WriteLine("\n[6/8] Verifying the audio byte-identity rule (M11 A2.3)...");
try
{
    // 6a: BalancedDefaults (the never-touched baseline) is BYTE-IDENTICAL to the pre-audio
    // Balanced args — an exact sequence match, not just "no flag starting with --audio", so an
    // unrelated stray flag fails this exactly like a real audio-flag regression would (§4.4).
    var untouchedArgs = MirrorService.BuildScrcpyArgs("PIXEL7", MirrorSettings.BalancedDefaults);
    var untouchedExpected = new List<string>
    {
        "-s", "PIXEL7", "-b", "8M", "-m", "1280",
        "--window-title", "Linc", "--window-borderless", "--stay-awake"
    };
    if (untouchedArgs.SequenceEqual(untouchedExpected))
    {
        Console.WriteLine($"    PASS: Never-touched settings (AudioEnabled=true, default source/bit-rate) are byte-identical to pre-audio args: {string.Join(" ", untouchedArgs)}");
    }
    else
    {
        Console.WriteLine($"    FAIL: Never-touched settings args mismatch.\n      Expected: {string.Join(" ", untouchedExpected)}\n      Actual:   {string.Join(" ", untouchedArgs)}");
        failures.Add("Byte-identity: never-touched MirrorSettings args are not byte-identical to pre-audio args");
    }

    // 6b: AudioEnabled=false adds --no-audio and nothing else audio-related.
    var mutedArgs = MirrorService.BuildScrcpyArgs("PIXEL7", MirrorSettings.BalancedDefaults with { AudioEnabled = false });
    if (mutedArgs.Contains("--no-audio") && !mutedArgs.Any(a => a.StartsWith("--audio-")))
    {
        Console.WriteLine($"    PASS: AudioEnabled=false adds --no-audio and nothing else: {string.Join(" ", mutedArgs)}");
    }
    else
    {
        Console.WriteLine($"    FAIL: AudioEnabled=false args wrong: {string.Join(" ", mutedArgs)}");
        failures.Add("Byte-identity: AudioEnabled=false did not emit --no-audio cleanly");
    }

    // 6c: a non-default bit rate adds ONLY --audio-bit-rate, while audio stays enabled.
    var bitRateArgs = MirrorService.BuildScrcpyArgs("PIXEL7", MirrorSettings.BalancedDefaults with { AudioBitRate = 256_000 });
    if (bitRateArgs.Contains("--audio-bit-rate=256000") && !bitRateArgs.Contains("--no-audio") && !bitRateArgs.Any(a => a.StartsWith("--audio-source")))
    {
        Console.WriteLine($"    PASS: Non-default AudioBitRate emits only --audio-bit-rate: {string.Join(" ", bitRateArgs)}");
    }
    else
    {
        Console.WriteLine($"    FAIL: Non-default AudioBitRate args wrong: {string.Join(" ", bitRateArgs)}");
        failures.Add("Byte-identity: non-default AudioBitRate emitted the wrong flags");
    }

    // 6d: a non-default source adds ONLY --audio-source, while audio stays enabled.
    var sourceArgs = MirrorService.BuildScrcpyArgs("PIXEL7", MirrorSettings.BalancedDefaults with { AudioSource = "mic" });
    if (sourceArgs.Contains("--audio-source=mic") && !sourceArgs.Contains("--no-audio") && !sourceArgs.Any(a => a.StartsWith("--audio-bit-rate")))
    {
        Console.WriteLine($"    PASS: Non-default AudioSource emits only --audio-source: {string.Join(" ", sourceArgs)}");
    }
    else
    {
        Console.WriteLine($"    FAIL: Non-default AudioSource args wrong: {string.Join(" ", sourceArgs)}");
        failures.Add("Byte-identity: non-default AudioSource emitted the wrong flags");
    }

    // 6e: AudioEnabled=false SUPPRESSES a non-default bit-rate/source flag too — --no-audio wins.
    var mutedWithExtras = MirrorService.BuildScrcpyArgs("PIXEL7", MirrorSettings.BalancedDefaults with { AudioEnabled = false, AudioBitRate = 256_000, AudioSource = "mic" });
    if (mutedWithExtras.Contains("--no-audio") && !mutedWithExtras.Any(a => a.StartsWith("--audio-bit-rate") || a.StartsWith("--audio-source")))
    {
        Console.WriteLine($"    PASS: AudioEnabled=false suppresses bit-rate/source flags too: {string.Join(" ", mutedWithExtras)}");
    }
    else
    {
        Console.WriteLine($"    FAIL: AudioEnabled=false did not suppress bit-rate/source: {string.Join(" ", mutedWithExtras)}");
        failures.Add("Byte-identity: AudioEnabled=false did not suppress bit-rate/source flags");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: Audio byte-identity check threw: {ex.Message}");
    failures.Add($"Audio byte-identity check threw: {ex.Message}");
}

// 7. M11 A2.4 — audio validation: AudioSource accepts null/"output"/"mic" only; AudioBitRate
// accepts 0 or 32,000-512,000.
Console.WriteLine("\n[7/8] Validating audio fields (MirrorSettings.ValidateSettingsFields)...");
try
{
    // 7a: null/"output"/"mic" all accepted.
    var nullSource = MirrorSettings.ValidateSettingsFields(0, "8M", 0, null, null, 0);
    var outputSource = MirrorSettings.ValidateSettingsFields(0, "8M", 0, null, "output", 0);
    var micSource = MirrorSettings.ValidateSettingsFields(0, "8M", 0, null, "mic", 0);
    if (nullSource is null && outputSource is null && micSource is null)
    {
        Console.WriteLine("    PASS: AudioSource null / \"output\" / \"mic\" all accepted.");
    }
    else
    {
        if (nullSource is not null) failures.Add($"Validation: AudioSource null wrongly rejected (\"{nullSource}\")");
        if (outputSource is not null) failures.Add($"Validation: AudioSource \"output\" wrongly rejected (\"{outputSource}\")");
        if (micSource is not null) failures.Add($"Validation: AudioSource \"mic\" wrongly rejected (\"{micSource}\")");
        Console.WriteLine("    FAIL: A valid AudioSource value was rejected.");
    }

    // 7b: any other AudioSource value is rejected.
    var badSource = MirrorSettings.ValidateSettingsFields(0, "8M", 0, null, "speaker", 0);
    if (badSource is not null)
    {
        Console.WriteLine($"    PASS: AudioSource \"speaker\" rejected: \"{badSource}\"");
    }
    else
    {
        Console.WriteLine("    FAIL: AudioSource \"speaker\" was NOT rejected.");
        failures.Add("Validation: invalid AudioSource not rejected");
    }

    // 7c: AudioBitRate 0 (the "use scrcpy default" sentry) is accepted.
    var bitRateZero = MirrorSettings.ValidateSettingsFields(0, "8M", 0, null, null, 0);
    if (bitRateZero is null)
    {
        Console.WriteLine("    PASS: AudioBitRate 0 (scrcpy default) accepted.");
    }
    else
    {
        Console.WriteLine($"    FAIL: AudioBitRate 0 was wrongly rejected: \"{bitRateZero}\"");
        failures.Add("Validation: AudioBitRate 0 wrongly rejected");
    }

    // 7d: the 32,000-512,000 boundaries are accepted; just outside them is rejected.
    var lowBoundary = MirrorSettings.ValidateSettingsFields(0, "8M", 0, null, null, 32_000);
    var highBoundary = MirrorSettings.ValidateSettingsFields(0, "8M", 0, null, null, 512_000);
    var belowLow = MirrorSettings.ValidateSettingsFields(0, "8M", 0, null, null, 31_999);
    var aboveHigh = MirrorSettings.ValidateSettingsFields(0, "8M", 0, null, null, 512_001);
    if (lowBoundary is null && highBoundary is null && belowLow is not null && aboveHigh is not null)
    {
        Console.WriteLine("    PASS: AudioBitRate boundaries (32,000/512,000 accepted; 31,999/512,001 rejected) hold.");
    }
    else
    {
        if (lowBoundary is not null) failures.Add($"Validation: AudioBitRate 32,000 wrongly rejected (\"{lowBoundary}\")");
        if (highBoundary is not null) failures.Add($"Validation: AudioBitRate 512,000 wrongly rejected (\"{highBoundary}\")");
        if (belowLow is null) failures.Add("Validation: AudioBitRate 31,999 not rejected");
        if (aboveHigh is null) failures.Add("Validation: AudioBitRate 512,001 not rejected");
        Console.WriteLine("    FAIL: An AudioBitRate boundary check failed.");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: Audio validation check threw: {ex.Message}");
    failures.Add($"Audio validation check threw: {ex.Message}");
}

// 8. Crude source-text check (BRAIN.md §4.1's second pattern): DevicePage.xaml is WinUI-side and
// this harness cannot construct or render it, so assert against the file's text instead — fail
// if the audio toggle's binding disappears from the production XAML.
Console.WriteLine("\n[8/8] Checking DevicePage.xaml binds the audio toggle (source-text check)...");
{
    var cwd = Directory.GetCurrentDirectory();
    var repoRoot = cwd;
    while (!Directory.Exists(Path.Combine(repoRoot, "DESKTOP")) && Directory.GetParent(repoRoot) != null)
    {
        repoRoot = Directory.GetParent(repoRoot)!.FullName;
    }
    var xamlPath = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "Views", "DevicePage.xaml");
    if (!File.Exists(xamlPath))
    {
        Console.WriteLine($"    FAIL: Cannot read {xamlPath}");
        failures.Add($"Missing XAML file: {xamlPath}");
    }
    else
    {
        var xaml = File.ReadAllText(xamlPath);
        const string needle = "MirrorSettings.AudioEnabled";
        if (xaml.Contains(needle))
        {
            Console.WriteLine($"    PASS: DevicePage.xaml still references {needle}.");
        }
        else
        {
            Console.WriteLine($"    FAIL: DevicePage.xaml no longer references {needle} — the audio toggle is unwired.");
            failures.Add("DevicePage.xaml missing MirrorSettings.AudioEnabled binding");
        }
    }
}

// Summary
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
