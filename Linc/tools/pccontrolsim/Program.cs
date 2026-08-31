using System.Reflection;
using Linc.Desktop.Services;

// Verification harness for M4b Part A (v17 quick controls): the pure PcControlValidator that
// gates every pc.control action before PcControlService ever attempts a real Win32/COM/WMI
// call — the confirm-required gate on destructive actions, the 0-100 level clamp, and the
// unknown-action/missing-level "internal" cases. Runs entirely offline: no PC state changes,
// no phone, no WinUI.
//
//   dotnet run --project tools/pccontrolsim
//
// Section 1 proves — by inspecting THIS ASSEMBLY's own compiled metadata, not by prose claim —
// that it cannot reach a real power action: PcControlValidator.cs and PcPowerCapabilities.cs
// (M12i Part B3) are the only production files compiled in (see the .csproj), and neither
// contains any P/Invoke.
//
// Section 6 (M12i Part B3) exercises PcPowerCapabilities.CanSleep — the pure
// (s3Allowed, s0LowPower) -> canSleep decision behind pc.state's canSleep field — over all four
// flag combinations, so the Modern Standby fix is testable without a machine that has either
// capability and without the harness reaching GetPwrCapabilities/Win32 at all.

Console.WriteLine("=== Linc PC-Control Validator Harness (pccontrolsim) ===");

var failures = new List<string>();

void Pass(string msg) => Console.WriteLine($"    PASS: {msg}");
void Fail(string msg, string failure)
{
    Console.WriteLine($"    FAIL: {msg}");
    failures.Add(failure);
}

// ---- 1. Structural proof: this harness cannot invoke a real power action ----

Console.WriteLine("\n[1] This assembly cannot reach a real power/volume/brightness call...");

var pInvokeMethods = typeof(PcControlValidator).Assembly.GetTypes()
    .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
    .Where(m => m.Attributes.HasFlag(MethodAttributes.PinvokeImpl))
    .ToList();

if (pInvokeMethods.Count == 0)
{
    Pass("this assembly's own compiled metadata contains zero P/Invoke methods — " +
        "PcControlValidator.cs and PcPowerCapabilities.cs are the only production files compiled " +
        "in (see the .csproj), and the Win32/COM/WMI interop that actually locks/sleeps/shuts " +
        "down/sets volume or brightness lives entirely in PcControlService.cs, which this " +
        "project never compiles.");
}
else
{
    Fail($"found {pInvokeMethods.Count} P/Invoke method(s) in this assembly: " +
        string.Join(", ", pInvokeMethods.Select(m => $"{m.DeclaringType?.Name}.{m.Name}")),
        "pccontrolsim's own assembly contains a P/Invoke — it could reach a real power action");
}

// ---- 2. Every action, valid case ----

Console.WriteLine("\n[2] Every known action validates with well-formed fields...");

void ExpectValid(string label, string? action, int? level = null, int? step = null,
    bool? on = null, bool? confirm = null)
{
    var result = PcControlValidator.ValidateControl(action, level, step, on, confirm);
    if (result is null) Pass($"{label} -> valid");
    else Fail($"{label} -> \"{result}\", expected valid (null)", $"{label} should be valid but got \"{result}\"");
}

void ExpectError(string label, string expected, string? action, int? level = null, int? step = null,
    bool? on = null, bool? confirm = null)
{
    var result = PcControlValidator.ValidateControl(action, level, step, on, confirm);
    if (result == expected) Pass($"{label} -> \"{expected}\"");
    else Fail($"{label} -> \"{result ?? "valid"}\", expected \"{expected}\"",
        $"{label} should be \"{expected}\" but got \"{result ?? "valid"}\"");
}

ExpectValid("lock", "lock");
ExpectValid("sleep (no confirm needed — not destructive)", "sleep");
ExpectValid("volume.up with no step (defaults to 1)", "volume.up");
ExpectValid("volume.up with an explicit step", "volume.up", step: 3);
ExpectValid("volume.down", "volume.down");
ExpectValid("volume.set at 50", "volume.set", level: 50);
ExpectValid("volume.set at the 0 boundary", "volume.set", level: 0);
ExpectValid("volume.set at the 100 boundary", "volume.set", level: 100);
ExpectValid("volume.mute with no 'on' (toggle)", "volume.mute");
ExpectValid("volume.mute with 'on' explicit", "volume.mute", on: true);
ExpectValid("brightness.set at 50", "brightness.set", level: 50);
ExpectValid("brightness.set at the 0 boundary", "brightness.set", level: 0);
ExpectValid("brightness.set at the 100 boundary", "brightness.set", level: 100);

// ---- 3. The confirm gate: destructive actions only ----

Console.WriteLine("\n[3] The confirm gate — this is the safety gate; prove it exists...");

ExpectError("shutdown with no confirm", "needs-confirm", "shutdown");
ExpectError("shutdown with confirm: false", "needs-confirm", "shutdown", confirm: false);
ExpectValid("shutdown with confirm: true", "shutdown", confirm: true);
ExpectError("restart with no confirm", "needs-confirm", "restart");
ExpectValid("restart with confirm: true", "restart", confirm: true);
// Non-destructive actions must never require confirm — confirming "lock" would be absurd UX.
ExpectValid("lock with no confirm (never required)", "lock");
ExpectValid("sleep with no confirm (spec: not destructive)", "sleep");
ExpectValid("volume.set with no confirm (never required)", "volume.set", level: 50);

// ---- 4. level range: 0-100, required where the spec requires it ----

Console.WriteLine("\n[4] The 0-100 level clamp and required-level cases...");

ExpectError("volume.set with no level", "internal", "volume.set");
ExpectError("volume.set below range (-1)", "internal", "volume.set", level: -1);
ExpectError("volume.set above range (101)", "internal", "volume.set", level: 101);
ExpectError("brightness.set with no level", "internal", "brightness.set");
ExpectError("brightness.set below range (-1)", "internal", "brightness.set", level: -1);
ExpectError("brightness.set above range (101)", "internal", "brightness.set", level: 101);
// level is irrelevant to actions that don't take one — an out-of-range level must not leak
// into a false rejection of an otherwise-valid action.
ExpectValid("lock with a stray out-of-range level (ignored)", "lock", level: 999);

// ---- 5. unknown / missing action ----

Console.WriteLine("\n[5] Unknown and missing action...");

ExpectError("unknown action", "internal", "reboot-now-please");
ExpectError("null action", "internal", null);
ExpectError("empty-string action", "internal", "");

// ---- 6. M12i Part B3: PcPowerCapabilities.CanSleep, the pure (s3Allowed, s0LowPower) -> canSleep decision ----

Console.WriteLine("\n[6] PcPowerCapabilities.CanSleep — pure (s3Allowed, s0LowPower) -> canSleep...");

void ExpectCanSleep(string label, bool s3Allowed, bool s0LowPower, bool expected)
{
    var result = PcPowerCapabilities.CanSleep(s3Allowed, s0LowPower);
    if (result == expected) Pass($"{label} -> {result}");
    else Fail($"{label} -> {result}, expected {expected}", $"{label} should be {expected} but got {result}");
}

ExpectCanSleep("legacy S3 only, no Modern Standby", s3Allowed: true, s0LowPower: false, expected: true);
ExpectCanSleep("Modern Standby only, no legacy S3 (this dev machine's real shape, M12i)", s3Allowed: false, s0LowPower: true, expected: true);
ExpectCanSleep("both available", s3Allowed: true, s0LowPower: true, expected: true);
ExpectCanSleep("neither available", s3Allowed: false, s0LowPower: false, expected: false);

// ---- 7. M16 Part A: PcMediaRouting.PickTarget, the pure "which player does the phone see?" rule ----

Console.WriteLine("\n[7] PcMediaRouting.PickTarget — one rule for what is published AND what is controlled...");

void ExpectTarget(string label, bool hasSmtc, bool smtcPlaying, bool hasWinamp, bool winampPlaying,
    PcMediaTarget expected)
{
    var result = PcMediaRouting.PickTarget(hasSmtc, smtcPlaying, hasWinamp, winampPlaying);
    if (result == expected) Pass($"{label} -> {result}");
    else Fail($"{label} -> {result}, expected {expected}", $"{label} should be {expected} but got {result}");
}

// THE M16 DEFECT, as a check: a paused SMTC player (a video paused in a browser, PotPlayer,
// Spotify) must still be the routing target, or `play` from the phone never reaches it and the
// player can be paused but never resumed. Routing used to require IsPlaying here.
ExpectTarget("a PAUSED SMTC session, nothing else — must still be reachable",
    hasSmtc: true, smtcPlaying: false, hasWinamp: false, winampPlaying: false, PcMediaTarget.Smtc);
ExpectTarget("a paused SMTC session with a paused Winamp-API player — SMTC still wins",
    hasSmtc: true, smtcPlaying: false, hasWinamp: true, winampPlaying: false, PcMediaTarget.Smtc);
ExpectTarget("a playing SMTC session",
    hasSmtc: true, smtcPlaying: true, hasWinamp: false, winampPlaying: false, PcMediaTarget.Smtc);
ExpectTarget("a playing SMTC session beside a playing Winamp-API player — SMTC wins",
    hasSmtc: true, smtcPlaying: true, hasWinamp: true, winampPlaying: true, PcMediaTarget.Smtc);
// The one case where a paused SMTC session is outranked: something is actually making sound.
ExpectTarget("a paused SMTC session while a Winamp-API player is PLAYING — Winamp wins",
    hasSmtc: true, smtcPlaying: false, hasWinamp: true, winampPlaying: true, PcMediaTarget.Winamp);
// The Winamp-API fallback survives (Part A2 requires it), playing or paused.
ExpectTarget("no SMTC session, a playing Winamp-API player",
    hasSmtc: false, smtcPlaying: false, hasWinamp: true, winampPlaying: true, PcMediaTarget.Winamp);
ExpectTarget("no SMTC session, a PAUSED Winamp-API player — still reachable",
    hasSmtc: false, smtcPlaying: false, hasWinamp: true, winampPlaying: false, PcMediaTarget.Winamp);
ExpectTarget("nothing registered anywhere -> the phone gets none:true and greys the buttons",
    hasSmtc: false, smtcPlaying: false, hasWinamp: false, winampPlaying: false, PcMediaTarget.None);

// ---- summary ----

Console.WriteLine("\n=== SUMMARY ===");
if (failures.Count == 0)
{
    Console.WriteLine("PASS: All verification checks succeeded.");
    return 0;
}
Console.WriteLine($"FAIL: {failures.Count} check(s) failed:");
foreach (var failure in failures) Console.WriteLine($"  - {failure}");
return 1;
