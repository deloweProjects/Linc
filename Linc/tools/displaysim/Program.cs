using System.Text.Json;
using System.Text.Json.Nodes;
using Linc.Desktop.Services;

Console.WriteLine("=== Linc Display-Control Verification Harness (displaysim) ===");

var failures = new List<string>();

// 1. Rotation payload: each of the three wire modes → exact JSON.
Console.WriteLine("\n[1/6] Rotation payload per mode...");
foreach (var mode in new[] { "auto", "portrait", "landscape" })
{
    var node = DisplayPayload.RotationPayload(mode);
    var json = node.ToJsonString();
    var expected = $"{{\"mode\":\"{mode}\"}}";
    if (json == expected)
    {
        Console.WriteLine($"    PASS: RotationPayload(\"{mode}\") -> {json}");
    }
    else
    {
        Console.WriteLine($"    FAIL: RotationPayload(\"{mode}\") mismatch.\n      Expected: {expected}\n      Actual:   {json}");
        failures.Add($"Rotation payload mismatch for mode '{mode}'");
    }
}

// 1b: an unknown mode is rejected before it reaches the wire.
try
{
    _ = DisplayPayload.RotationPayload("sideways");
    Console.WriteLine("    FAIL: RotationPayload(\"sideways\") was NOT rejected.");
    failures.Add("RotationPayload accepted unknown mode 'sideways'");
}
catch (ArgumentException ex)
{
    Console.WriteLine($"    PASS: RotationPayload(\"sideways\") rejected: \"{ex.Message}\"");
}

// 2. Brightness payload: auto:true omits the level key entirely; auto:false includes it.
Console.WriteLine("\n[2/6] Brightness payload (auto:true omits level; auto:false includes it)...");

// 2a: auto:true with a null level — level key must be absent.
var autoTrue = DisplayPayload.BrightnessPayload(true, null);
var autoTrueJson = autoTrue.ToJsonString();
var autoTrueExpected = "{\"auto\":true}";
if (autoTrueJson == autoTrueExpected)
{
    Console.WriteLine($"    PASS: BrightnessPayload(true, null) -> {autoTrueJson} (no 'level' key)");
}
else
{
    Console.WriteLine($"    FAIL: BrightnessPayload(true, null) mismatch.\n      Expected: {autoTrueExpected}\n      Actual:   {autoTrueJson}");
    failures.Add("BrightnessPayload(true, null) included a level key");
}

// 2b: auto:true with a level — caller is allowed to pass one, but the payload must still
// omit it (the phone ignores level when auto is true, and the wire should match what
// the spec says: "auto: true -> level is ignored").
var autoTrueWithLevel = DisplayPayload.BrightnessPayload(true, 75);
var autoTrueWithLevelJson = autoTrueWithLevel.ToJsonString();
if (autoTrueWithLevelJson == autoTrueExpected)
{
    Console.WriteLine($"    PASS: BrightnessPayload(true, 75) drops the ignored level: {autoTrueWithLevelJson}");
}
else
{
    Console.WriteLine($"    FAIL: BrightnessPayload(true, 75) did not drop level.\n      Actual: {autoTrueWithLevelJson}");
    failures.Add("BrightnessPayload(true, _) leaked a level key on the wire");
}

// 2c: auto:false with an in-range level.
var autoFalse = DisplayPayload.BrightnessPayload(false, 50);
var autoFalseJson = autoFalse.ToJsonString();
var autoFalseExpected = "{\"auto\":false,\"level\":50}";
if (autoFalseJson == autoFalseExpected)
{
    Console.WriteLine($"    PASS: BrightnessPayload(false, 50) -> {autoFalseJson}");
}
else
{
    Console.WriteLine($"    FAIL: BrightnessPayload(false, 50) mismatch.\n      Expected: {autoFalseExpected}\n      Actual:   {autoFalseJson}");
    failures.Add("BrightnessPayload(false, 50) payload mismatch");
}

// 2d: boundary values (0 and 100) — wire exactly.
foreach (var boundary in new[] { 0, 100 })
{
    var node = DisplayPayload.BrightnessPayload(false, boundary);
    var json = node.ToJsonString();
    var expected = $"{{\"auto\":false,\"level\":{boundary}}}";
    if (json == expected)
    {
        Console.WriteLine($"    PASS: BrightnessPayload(false, {boundary}) -> {json}");
    }
    else
    {
        Console.WriteLine($"    FAIL: BrightnessPayload(false, {boundary}) mismatch.\n      Expected: {expected}\n      Actual:   {json}");
        failures.Add($"BrightnessPayload boundary {boundary} mismatch");
    }
}

// 3. ArgumentException for a null/out-of-range level when auto is false.
Console.WriteLine("\n[3/6] ArgumentException for null or out-of-range level when auto is false...");
foreach (var badLevel in new int?[] { null, -1, 101, 255, int.MinValue, int.MaxValue })
{
    try
    {
        _ = DisplayPayload.BrightnessPayload(false, badLevel);
        var label = badLevel?.ToString() ?? "null";
        Console.WriteLine($"    FAIL: BrightnessPayload(false, {label}) was NOT rejected.");
        failures.Add($"BrightnessPayload(false, {label}) not rejected");
    }
    catch (ArgumentException ex)
    {
        var label = badLevel?.ToString() ?? "null";
        Console.WriteLine($"    PASS: BrightnessPayload(false, {label}) rejected: \"{ex.Message}\"");
    }
}

// 4. Version-gate predicate.
Console.WriteLine("\n[4/6] Version-gate predicate (negotiated >= 15)...");
var supportedAt15 = DisplayPayload.IsSupported(15);
var unsupportedAt14 = !DisplayPayload.IsSupported(14);
var supportedAt16 = DisplayPayload.IsSupported(16);
var unsupportedAt0 = !DisplayPayload.IsSupported(0);
var unsupportedAtNull = !DisplayPayload.IsSupported(null);

if (supportedAt15 && supportedAt16)
{
    Console.WriteLine("    PASS: IsSupported(15) and IsSupported(16) return true.");
}
else
{
    Console.WriteLine($"    FAIL: IsSupported(15)={supportedAt15}, IsSupported(16)={supportedAt16} — expected both true.");
    failures.Add("IsSupported at >= 15 returned false");
}

if (unsupportedAt14 && unsupportedAt0 && unsupportedAtNull)
{
    Console.WriteLine("    PASS: IsSupported returns false at 14, at 0, and when negotiatedVersion is null.");
}
else
{
    Console.WriteLine($"    FAIL: 14={!unsupportedAt14} (expected false), 0={!unsupportedAt0} (expected false), null={!unsupportedAtNull} (expected false).");
    failures.Add("IsSupported returned true on a pre-v15 or disconnected link");
}

// 4b: UnsupportedReason string matches the ConnectionManager.SendToPhoneAsync precedent
// (the same wording the supervisor logs and the UI surfaces — D-055).
var reasonNull = DisplayPayload.UnsupportedReason(null);
var reason14 = DisplayPayload.UnsupportedReason(14);
if (reasonNull == "no phone connected" && reason14 == "the phone negotiated v14 (needs 15)")
{
    Console.WriteLine($"    PASS: UnsupportedReason strings match the established wording.");
}
else
{
    Console.WriteLine($"    FAIL: UnsupportedReason mismatch.\n      null: '{reasonNull}' (expected 'no phone connected')\n      14:   '{reason14}' (expected 'the phone negotiated v14 (needs 15)')");
    failures.Add("UnsupportedReason wording drifted");
}

// 5. Status parser: payload missing all three fields -> three nulls.
Console.WriteLine("\n[5/6] Status parser maps a payload missing all three v15 fields to three nulls...");

// 5a: an entirely empty payload.
var empty = new JsonObject();
var (r0, a0, l0) = DisplayPayload.ParseStatus(empty);
if (r0 is null && a0 is null && l0 is null)
{
    Console.WriteLine("    PASS: Empty payload -> (null, null, null).");
}
else
{
    Console.WriteLine($"    FAIL: Empty payload did not yield three nulls. Got ({r0}, {a0}, {l0}).");
    failures.Add("ParseStatus did not yield three nulls on an empty payload");
}

// 5b: a null payload (defensive — should not happen in practice, but the type allows it).
var (rNull, aNull, lNull) = DisplayPayload.ParseStatus(null);
if (rNull is null && aNull is null && lNull is null)
{
    Console.WriteLine("    PASS: Null payload -> (null, null, null).");
}
else
{
    Console.WriteLine($"    FAIL: Null payload did not yield three nulls. Got ({rNull}, {aNull}, {lNull}).");
    failures.Add("ParseStatus did not yield three nulls on a null payload");
}

// 5c: a payload with the v14 fields only — must NOT fabricate v15 values.
var legacy = new JsonObject
{
    ["battery"] = 87,
    ["charging"] = true,
    ["storageFreeBytes"] = 12345,
    ["storageTotalBytes"] = 67890,
};
var (rLegacy, aLegacy, lLegacy) = DisplayPayload.ParseStatus(legacy);
if (rLegacy is null && aLegacy is null && lLegacy is null)
{
    Console.WriteLine("    PASS: v14-only payload (battery/charging/storage) -> (null, null, null).");
}
else
{
    Console.WriteLine($"    FAIL: v14-only payload did not yield three nulls. Got ({rLegacy}, {aLegacy}, {lLegacy}).");
    failures.Add("ParseStatus fabricated v15 fields from a v14 payload");
}

// 5d: a payload with the three v15 fields present — round-trip back to the same values.
var full = new JsonObject
{
    ["rotationMode"] = "portrait",
    ["brightnessAuto"] = false,
    ["brightnessLevel"] = 75,
};
var (rFull, aFull, lFull) = DisplayPayload.ParseStatus(full);
if (rFull == "portrait" && aFull == false && lFull == 75)
{
    Console.WriteLine($"    PASS: v15 payload round-trips: ({rFull}, {aFull}, {lFull}).");
}
else
{
    Console.WriteLine($"    FAIL: v15 payload did not round-trip. Got ({rFull}, {aFull}, {lFull}).");
    failures.Add("ParseStatus did not round-trip the v15 fields");
}

// 5e: a payload with the three v15 fields present but of wrong type — must stay null
// (the desktop shouldn't trust a malformed type any more than a missing field).
var wrongTypes = new JsonObject
{
    ["rotationMode"] = 42,            // should be string
    ["brightnessAuto"] = "yes",       // should be bool
    ["brightnessLevel"] = "seventy",  // should be int
};
var (rBad, aBad, lBad) = DisplayPayload.ParseStatus(wrongTypes);
if (rBad is null && aBad is null && lBad is null)
{
    Console.WriteLine("    PASS: Payload with wrong-typed v15 fields -> (null, null, null) — no crash, no false value.");
}
else
{
    Console.WriteLine($"    FAIL: Wrong-typed fields did not yield three nulls. Got ({rBad}, {aBad}, {lBad}).");
    failures.Add("ParseStatus coerced wrong-typed v15 fields instead of leaving them null");
}

// 6. UI wiring — deliberately crude text checks so a future session that unwires a control fails loudly.
//    This section reads the two view files as text from disk and asserts the wiring exists.
//    If a file cannot be read, that is a FAIL, not a skip.
Console.WriteLine("\n[6/6] UI wiring checks...");
// Resolve repo root from the current working directory (where dotnet run is invoked).
// We look for the Linc folder which contains DESKTOP/tools/SCRCPY/ANDROID.
var cwd = Directory.GetCurrentDirectory();
var repoRoot = cwd;
while (!Directory.Exists(Path.Combine(repoRoot, "DESKTOP")) && Directory.GetParent(repoRoot) != null)
{
    repoRoot = Directory.GetParent(repoRoot)!.FullName;
}
var xamlPath = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "Views", "DevicePage.xaml");
var csPath = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "Views", "DevicePage.xaml.cs");

if (!File.Exists(xamlPath))
{
    Console.WriteLine($"    FAIL: Cannot read {xamlPath}");
    failures.Add($"Missing XAML file: {xamlPath}");
}
else if (!File.Exists(csPath))
{
    Console.WriteLine($"    FAIL: Cannot read {csPath}");
    failures.Add($"Missing C# file: {csPath}");
}
else
{
    var xaml = File.ReadAllText(xamlPath);
    var cs = File.ReadAllText(csPath);

    // 6.1 SelectionChanged on rotation ComboBox
    const string s1 = "SelectionChanged=\"RotationCombo_SelectionChanged\"";
    if (xaml.Contains(s1))
    {
        Console.WriteLine($"    PASS: XAML contains {s1}");
    }
    else
    {
        Console.WriteLine($"    FAIL: XAML missing {s1}");
        failures.Add("XAML missing RotationCombo SelectionChanged");
    }

    // 6.2 Toggled on adaptive-brightness ToggleSwitch
    const string s2 = "Toggled=\"BrightnessAutoToggle_Toggled\"";
    if (xaml.Contains(s2))
    {
        Console.WriteLine($"    PASS: XAML contains {s2}");
    }
    else
    {
        Console.WriteLine($"    FAIL: XAML missing {s2}");
        failures.Add("XAML missing BrightnessAutoToggle Toggled");
    }

    // 6.3 ValueChanged on brightness Slider
    const string s3 = "ValueChanged=\"BrightnessSlider_ValueChanged\"";
    if (xaml.Contains(s3))
    {
        Console.WriteLine($"    PASS: XAML contains {s3}");
    }
    else
    {
        Console.WriteLine($"    FAIL: XAML missing {s3}");
        failures.Add("XAML missing BrightnessSlider ValueChanged");
    }

    // 6.4 SetRotationCommand referenced in code-behind
    if (cs.Contains("SetRotationCommand"))
    {
        Console.WriteLine($"    PASS: Code-behind references SetRotationCommand");
    }
    else
    {
        Console.WriteLine($"    FAIL: Code-behind missing SetRotationCommand");
        failures.Add("Code-behind missing SetRotationCommand");
    }

    // 6.5 SetBrightnessAutoCommand referenced in code-behind
    if (cs.Contains("SetBrightnessAutoCommand"))
    {
        Console.WriteLine($"    PASS: Code-behind references SetBrightnessAutoCommand");
    }
    else
    {
        Console.WriteLine($"    FAIL: Code-behind missing SetBrightnessAutoCommand");
        failures.Add("Code-behind missing SetBrightnessAutoCommand");
    }

    // 6.6 SetBrightnessLevelCommand referenced in code-behind
    if (cs.Contains("SetBrightnessLevelCommand"))
    {
        Console.WriteLine($"    PASS: Code-behind references SetBrightnessLevelCommand");
    }
    else
    {
        Console.WriteLine($"    FAIL: Code-behind missing SetBrightnessLevelCommand");
        failures.Add("Code-behind missing SetBrightnessLevelCommand");
    }

    // 6.7 Each handler has an early-return guard comparing against the VM property
    // Rotation handler compares against RotationMode
    if (cs.Contains("RotationCombo_SelectionChanged") && cs.Contains("DisplayControl.RotationMode"))
    {
        Console.WriteLine($"    PASS: Rotation handler has guard against RotationMode");
    }
    else
    {
        Console.WriteLine($"    FAIL: Rotation handler missing guard against RotationMode");
        failures.Add("Rotation handler missing RotationMode guard");
    }

    // Brightness auto handler compares against BrightnessAuto
    if (cs.Contains("BrightnessAutoToggle_Toggled") && cs.Contains("DisplayControl.BrightnessAuto"))
    {
        Console.WriteLine($"    PASS: BrightnessAuto handler has guard against BrightnessAuto");
    }
    else
    {
        Console.WriteLine($"    FAIL: BrightnessAuto handler missing guard against BrightnessAuto");
        failures.Add("BrightnessAuto handler missing BrightnessAuto guard");
    }

    // Brightness level handler compares against BrightnessLevel
    if (cs.Contains("BrightnessSlider_ValueChanged") && cs.Contains("DisplayControl.BrightnessLevel"))
    {
        Console.WriteLine($"    PASS: BrightnessSlider handler has guard against BrightnessLevel");
    }
    else
    {
        Console.WriteLine($"    FAIL: BrightnessSlider handler missing guard against BrightnessLevel");
        failures.Add("BrightnessSlider handler missing BrightnessLevel guard");
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
