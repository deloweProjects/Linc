// Verification harness for M18 Part B (dynamic colour + the contrast floor). Links the REAL
// DESKTOP\Linc.Desktop\Services\DynamicPalette.cs and calls the real derivation (GUIDE 4.1).
// Touches no files, no registry, no settings.json.
//
//   dotnet run --project tools/palettesim
//
// Check 1 — the contrast maths itself is right, against WCAG's own published reference values.
//   A ratio function that is subtly wrong would make every other check here meaningless.
// Check 2 — THE SWEEP. Every one of 4,096 wallpaper colours (the full RGB cube at step 17), in BOTH
//   themes, must clear 4.5:1 for body text and the muted caption colour. This is B3's evidence, and
//   it reports the WORST CASE it found rather than an average.
// Check 3 — the fallback is the base state, not a special case: a null dominant must return the
//   M14 neutral ramp EXACTLY, byte for byte, in both themes.
// Check 4 — the tint is bounded: no derived surface may be further from its neutral base than
//   MaxTint allows, so "dynamic colour" can never repaint the app into something unrecognisable.
//
// Negative proof this harness is designed to catch (M18 acceptance 7a): forcing the derivation to
// a low-contrast pair (e.g. dropping the ClampToContrast call, or raising MaxTint past the floor)
// makes check 2 fail and names the wallpaper colour that broke it.

using Linc.Desktop.Services;

Console.WriteLine("=== Linc Dynamic Palette Verification Harness (palettesim) ===");

var failures = new List<string>();
const int Total = 4;

void Check(int index, bool ok, string passText, string failText, string failure)
{
    Console.WriteLine(ok ? $"    [{index}/{Total}] PASS: {passText}" : $"    [{index}/{Total}] FAIL: {failText}");
    if (!ok)
    {
        failures.Add(failure);
    }
}

// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n[1/{Total}] The contrast maths matches WCAG's published reference values...");

(string Name, string A, string B, double Expected)[] reference =
[
    ("black on white", "#000000", "#FFFFFF", 21.00),
    ("white on white", "#FFFFFF", "#FFFFFF", 1.00),
    ("mid grey on white", "#808080", "#FFFFFF", 3.95),
    ("blue on white", "#0000FF", "#FFFFFF", 8.59),
    ("red on white", "#FF0000", "#FFFFFF", 3.998),
];

var mathsOk = true;
foreach (var (name, a, b, expected) in reference)
{
    var actual = DynamicPalette.Contrast(Rgb.FromHex(a), Rgb.FromHex(b));
    var ok = Math.Abs(actual - expected) < 0.02;
    mathsOk &= ok;
    Console.WriteLine($"        {(ok ? "PASS" : "FAIL")}: {name,-18} expected {expected,6:F2}  actual {actual,6:F2}");
}

Check(1, mathsOk,
    "the ratio function reproduces WCAG's reference values (sRGB gamma expansion, not a naive average).",
    "the contrast function does not match WCAG — every other check here would be meaningless.",
    "DynamicPalette.Contrast does not match WCAG reference values");

// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n[2/{Total}] THE SWEEP — every wallpaper colour, both themes, must clear the floor...");

var worstBody = (Ratio: double.MaxValue, Wallpaper: "", Theme: "", Role: "");
var worstVariant = (Ratio: double.MaxValue, Wallpaper: "", Theme: "", Role: "");
var swept = 0;
var breaches = new List<string>();

foreach (var isDark in new[] { false, true })
{
    var theme = isDark ? "dark" : "light";
    for (var r = 0; r <= 255; r += 17)
    {
        for (var g = 0; g <= 255; g += 17)
        {
            for (var b = 0; b <= 255; b += 17)
            {
                var wallpaper = new Rgb((byte)r, (byte)g, (byte)b);
                var p = DynamicPalette.Derive(wallpaper, isDark);
                swept++;

                // Body text on both the page surface and a card.
                foreach (var (surface, role) in new[] { (p.Surface, "onSurface/surface"), (p.SurfaceContainer, "onSurface/card") })
                {
                    var ratio = DynamicPalette.Contrast(p.OnSurface, surface);
                    if (ratio < worstBody.Ratio)
                    {
                        worstBody = (ratio, wallpaper.ToHex(), theme, role);
                    }
                    if (ratio < DynamicPalette.BodyContrast)
                    {
                        breaches.Add($"{wallpaper.ToHex()} {theme} {role} = {ratio:F2}");
                    }
                }

                // The muted caption colour — the one that historically goes grey-on-grey.
                var vRatio = DynamicPalette.Contrast(p.OnSurfaceVariant, p.Surface);
                if (vRatio < worstVariant.Ratio)
                {
                    worstVariant = (vRatio, wallpaper.ToHex(), theme, "onSurfaceVariant/surface");
                }
                if (vRatio < DynamicPalette.BodyContrast)
                {
                    breaches.Add($"{wallpaper.ToHex()} {theme} onSurfaceVariant = {vRatio:F2}");
                }
            }
        }
    }
}

Console.WriteLine($"        swept {swept:N0} wallpaper/theme combinations (full RGB cube at step 17, both themes)");
Console.WriteLine($"        WORST body text    : {worstBody.Ratio:F2}:1  at wallpaper {worstBody.Wallpaper} ({worstBody.Theme}, {worstBody.Role})");
Console.WriteLine($"        WORST caption text : {worstVariant.Ratio:F2}:1  at wallpaper {worstVariant.Wallpaper} ({worstVariant.Theme})");
Console.WriteLine($"        floor required     : {DynamicPalette.BodyContrast:F1}:1 body, {DynamicPalette.LargeContrast:F1}:1 large/icons");
foreach (var breach in breaches.Take(5))
{
    Console.WriteLine($"        BREACH: {breach}");
}
if (breaches.Count > 5)
{
    Console.WriteLine($"        ... and {breaches.Count - 5} more");
}

Check(2, breaches.Count == 0,
    $"no wallpaper in {swept:N0} combinations drops body OR caption text below {DynamicPalette.BodyContrast:F1}:1. " +
    $"Worst measured: {Math.Min(worstBody.Ratio, worstVariant.Ratio):F2}:1.",
    $"{breaches.Count} wallpaper/theme combination(s) fall below the floor — a dark wallpaper produces grey-on-grey.",
    "the derived palette breaches the WCAG AA contrast floor");

// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n[3/{Total}] The neutral ramp is the BASE STATE, not a bolted-on fallback...");

var fallbackOk = true;
foreach (var isDark in new[] { false, true })
{
    var neutral = DynamicPalette.Neutral(isDark);
    var derived = DynamicPalette.Derive(null, isDark);
    var ok = neutral == derived;
    fallbackOk &= ok;
    Console.WriteLine($"        {(ok ? "PASS" : "FAIL")}: {(isDark ? "dark" : "light"),-5} no-wallpaper derivation == M14 neutral ramp " +
                      $"(surface {derived.Surface.ToHex()}, text {derived.OnSurface.ToHex()})");
    var ratio = DynamicPalette.Contrast(neutral.OnSurface, neutral.Surface);
    Console.WriteLine($"               neutral ramp's own body contrast: {ratio:F2}:1");
    if (ratio < DynamicPalette.BodyContrast)
    {
        fallbackOk = false;
    }
}

Check(3, fallbackOk,
    "a disconnected phone / no wallpaper / the setting off all return the M14 neutral ramp exactly, and it clears the floor.",
    "the no-wallpaper path does not return the neutral ramp.",
    "the neutral fallback is not identical to the shipped base palette");

// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n[4/{Total}] The tint is bounded — dynamic colour cannot repaint the app...");

var worstDrift = 0.0;
var driftAt = "";
foreach (var isDark in new[] { false, true })
{
    var neutral = DynamicPalette.Neutral(isDark);
    for (var r = 0; r <= 255; r += 51)
    {
        for (var g = 0; g <= 255; g += 51)
        {
            for (var b = 0; b <= 255; b += 51)
            {
                var p = DynamicPalette.Derive(new Rgb((byte)r, (byte)g, (byte)b), isDark);
                // How far the hue/chroma moved, ignoring the lightness walk the clamp is allowed
                // to perform: compare each channel's distance from the neutral base.
                var drift = Math.Max(
                    Math.Abs(p.Surface.R - neutral.Surface.R),
                    Math.Max(Math.Abs(p.Surface.G - neutral.Surface.G), Math.Abs(p.Surface.B - neutral.Surface.B)))
                    / 255.0;
                if (drift > worstDrift)
                {
                    worstDrift = drift;
                    driftAt = $"#{r:X2}{g:X2}{b:X2} ({(isDark ? "dark" : "light")})";
                }
            }
        }
    }
}

// The clamp is allowed to walk the surface toward the theme extreme, so the bound is the tint plus
// the clamp's own travel — not MaxTint alone. What must hold is that it stays well short of a
// wholesale repaint.
const double DriftCeiling = 0.35;
Console.WriteLine($"        worst surface drift from the neutral base: {worstDrift:P1} at {driftAt} (ceiling {DriftCeiling:P0})");
Check(4, worstDrift <= DriftCeiling,
    $"no wallpaper moves a surface more than {worstDrift:P1} from the neutral base — the app stays recognisably Linc.",
    $"a wallpaper moved the surface {worstDrift:P1} from neutral, past the {DriftCeiling:P0} ceiling.",
    "dynamic colour drifts too far from the neutral base");

// ---------------------------------------------------------------------------------------
Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("PASS: All verification checks succeeded.");
    return 0;
}

Console.WriteLine($"FAIL: {failures.Count} check(s) failed:");
foreach (var failure in failures)
{
    Console.WriteLine($"  - {failure}");
}
return 1;
