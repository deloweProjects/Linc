using Linc.Desktop.Services;

// Verification harness for M10 Part A (install-APK): the pure ApkInstall helpers that back
// the "Install app" row on the Device page — path validation before anything touches the
// phone (A2.3), the staged-progress order (A2.4), and every adb-failure translation (A2.5),
// plus a couple of crude source-text checks over the production UI/VM files. Runs entirely
// offline — no phone, no WinUI — against a throwaway temp directory (D-057 posture: nothing
// here can reach the owner's real filesystem state beyond a Temp scratch folder it cleans up).
//
//   dotnet run --project tools/apkinstallsim
//
// CWD must be Linc for the crude source-text checks (section 4) to
// find DevicePage.xaml / DeviceViewModel.cs; FindRepoRoot below handles being launched from
// either yellow\ or Linc\ (same posture as storesim's).

Console.WriteLine("=== Linc APK-Install Verification Harness (apkinstallsim) ===");

var failures = new List<string>();

void Check(int index, int total, bool ok, string passText, string failText, string failure)
{
    if (ok)
    {
        Console.WriteLine($"    [{index}/{total}] PASS: {passText}");
    }
    else
    {
        Console.WriteLine($"    [{index}/{total}] FAIL: {failText}");
        failures.Add(failure);
    }
}

void CheckSub(int index, int total, bool ok, string passText, string failText, string failure)
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

var root = Path.Combine(Path.GetTempPath(), "Linc_apkinstallsim_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
    // ---------------------------------------------------------------------------------------
    // 1. TranslateInstallFailure — every A2.5 mapping, including the fallback. The raw string
    //    must never come back verbatim (that would mean the translation was skipped).
    // ---------------------------------------------------------------------------------------
    Console.WriteLine("\n[1/5] TranslateInstallFailure — every A2.5 mapping...");
    var mappings = new (string Raw, string Expected)[]
    {
        ("Failure [INSTALL_FAILED_ALREADY_EXISTS: ...]", "That app is already installed."),
        ("Failure [INSTALL_FAILED_INSUFFICIENT_STORAGE: ...]", "Not enough space on the phone."),
        ("Failure [INSTALL_FAILED_INVALID_APK: ...]", "That file isn't a valid Android app."),
        ("Failure [INSTALL_PARSE_FAILED_NOT_APK: ...]", "That file isn't a valid Android app."),
        ("Failure [INSTALL_FAILED_UPDATE_INCOMPATIBLE: ...]", "A different version is already installed. Uninstall it first."),
        ("Failure [INSTALL_FAILED_UPDATE_INCOMPATIBLE: signatures do not match]", "A different version is already installed. Uninstall it first."),
        ("Failure [INSTALL_FAILED_USER_RESTRICTED: ...]", "The phone refused the install. Check it for a prompt."),
        ("some completely unrecognised adb output", "Install failed. The phone rejected the app."), // fallback
    };
    var m = 1;
    foreach (var (raw, expected) in mappings)
    {
        var actual = ApkInstall.TranslateInstallFailure(raw);
        CheckSub(m++, mappings.Length,
            actual == expected && actual != raw,
            $"\"{raw}\" -> \"{actual}\"",
            $"\"{raw}\" -> \"{actual}\" (expected \"{expected}\")",
            $"TranslateInstallFailure mismatch for '{raw}'");
    }
    Check(1, 5, failures.Count == 0, "all A2.5 mappings translate correctly.",
        "one or more A2.5 mappings are wrong (see above).", "TranslateInstallFailure mapping failures");

    // ---------------------------------------------------------------------------------------
    // 2. ValidateApkPath — missing / non-.apk / zero-byte / disconnected, each a DISTINCT
    //    message, plus a fully-valid case.
    // ---------------------------------------------------------------------------------------
    Console.WriteLine("\n[2/5] ValidateApkPath — each rejection has its own message...");
    var before = failures.Count;

    var validApk = Path.Combine(root, "app.apk");
    File.WriteAllBytes(validApk, [1, 2, 3, 4]);
    var zeroByteApk = Path.Combine(root, "empty.apk");
    File.WriteAllBytes(zeroByteApk, []);
    var wrongExtension = Path.Combine(root, "app.txt");
    File.WriteAllBytes(wrongExtension, [1, 2, 3]);
    var missingPath = Path.Combine(root, "does-not-exist.apk");

    var missing = ApkInstall.ValidateApkPath(missingPath, connected: true);
    var wrongExt = ApkInstall.ValidateApkPath(wrongExtension, connected: true);
    var zeroByte = ApkInstall.ValidateApkPath(zeroByteApk, connected: true);
    var disconnected = ApkInstall.ValidateApkPath(validApk, connected: false);
    var valid = ApkInstall.ValidateApkPath(validApk, connected: true);

    CheckSub(1, 5, !missing.IsValid && missing.Message is not null,
        $"missing file rejected: \"{missing.Message}\"",
        $"missing file NOT rejected (IsValid={missing.IsValid}, Message={missing.Message})",
        "ValidateApkPath did not reject a missing file");
    CheckSub(2, 5, !wrongExt.IsValid && wrongExt.Message is not null,
        $"non-.apk extension rejected: \"{wrongExt.Message}\"",
        $"non-.apk extension NOT rejected (IsValid={wrongExt.IsValid})",
        "ValidateApkPath did not reject a non-.apk extension");
    CheckSub(3, 5, !zeroByte.IsValid && zeroByte.Message is not null,
        $"zero-byte file rejected: \"{zeroByte.Message}\"",
        $"zero-byte file NOT rejected (IsValid={zeroByte.IsValid})",
        "ValidateApkPath did not reject a zero-byte file");
    CheckSub(4, 5, !disconnected.IsValid && disconnected.Message is not null,
        $"disconnected rejected: \"{disconnected.Message}\"",
        $"disconnected NOT rejected (IsValid={disconnected.IsValid})",
        "ValidateApkPath did not reject a disconnected device");
    CheckSub(5, 5, valid.IsValid && valid.Message is null,
        "a real, non-empty .apk with a connected device validates OK.",
        $"a valid case was rejected: \"{valid.Message}\"",
        "ValidateApkPath rejected a valid case");

    var distinctMessages = new[] { missing.Message, wrongExt.Message, zeroByte.Message, disconnected.Message }
        .Distinct(StringComparer.Ordinal).Count();
    Check(2, 5, distinctMessages == 4 && failures.Count == before,
        "all four rejections are distinct messages and each rejection behaved correctly.",
        $"only {distinctMessages}/4 messages are distinct, or a rejection case misbehaved (see above).",
        "ValidateApkPath messages are not all distinct");

    // ---------------------------------------------------------------------------------------
    // 3. Staged order (A2.4): Validating -> Copying to phone -> Installing -> Done/Failed,
    //    and every stage has non-empty, distinct display text.
    // ---------------------------------------------------------------------------------------
    Console.WriteLine("\n[3/5] Staged order (A2.4)...");
    var stages = Enum.GetValues<ApkInstall.Stage>();
    var expectedOrder = new[]
    {
        ApkInstall.Stage.Validating, ApkInstall.Stage.CopyingToPhone, ApkInstall.Stage.Installing,
        ApkInstall.Stage.Done, ApkInstall.Stage.Failed,
    };
    Check(3, 5, stages.SequenceEqual(expectedOrder),
        $"Stage enum declares the order Validating -> CopyingToPhone -> Installing -> Done -> Failed.",
        $"Stage enum order is {string.Join(", ", stages)} (expected {string.Join(", ", expectedOrder)}).",
        "ApkInstall.Stage declaration order is wrong");

    var stageTexts = stages.Select(ApkInstall.StageText).ToList();
    CheckSub(1, 2, stageTexts.All(t => !string.IsNullOrWhiteSpace(t)),
        "every stage has non-empty display text.",
        $"one or more stages produced empty text: {string.Join(" | ", stageTexts)}",
        "ApkInstall.StageText produced empty text for a stage");
    CheckSub(2, 2, stageTexts.Distinct(StringComparer.Ordinal).Count() == stageTexts.Count,
        $"all stage texts are distinct: {string.Join(" | ", stageTexts)}",
        $"stage texts are not all distinct: {string.Join(" | ", stageTexts)}",
        "ApkInstall.StageText produced duplicate text across stages");

    // ---------------------------------------------------------------------------------------
    // 4. Crude source-text checks over the production UI/VM files (M9b-style: the harness
    //    cannot call into WinUI, so it reads the files as text and fails loudly if the wiring
    //    a future session could accidentally delete — the command binding, the in-flight
    //    guard — is gone). Deliberately crude: string containment, not a parser.
    // ---------------------------------------------------------------------------------------
    Console.WriteLine("\n[4/5] Crude UI/VM wiring checks (source-text, not a parser)...");
    var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory());
    var xamlPath = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "Views", "DevicePage.xaml");
    var vmPath = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "ViewModels", "DeviceViewModel.cs");
    var beforeSection4 = failures.Count;

    if (!File.Exists(xamlPath))
    {
        Check(4, 5, false, "", $"cannot read {xamlPath}", $"Missing file: {xamlPath}");
    }
    else if (!File.Exists(vmPath))
    {
        Check(4, 5, false, "", $"cannot read {vmPath}", $"Missing file: {vmPath}");
    }
    else
    {
        var xaml = File.ReadAllText(xamlPath);
        var vm = File.ReadAllText(vmPath);

        CheckSub(1, 2, xaml.Contains("ViewModel.InstallApkCommand", StringComparison.Ordinal),
            "DevicePage.xaml binds Command=\"{x:Bind ViewModel.InstallApkCommand}\".",
            "DevicePage.xaml no longer references ViewModel.InstallApkCommand — the Install app button is unwired.",
            "DevicePage.xaml missing InstallApkCommand binding");

        CheckSub(2, 2, vm.Contains("IsInstallingApk = true", StringComparison.Ordinal),
            "DeviceViewModel.cs sets IsInstallingApk = true (the in-flight guard) before starting a run.",
            "DeviceViewModel.cs no longer sets IsInstallingApk = true — the in-flight guard is gone; two installs could overlap.",
            "DeviceViewModel.cs missing the IsInstallingApk in-flight guard");

        Check(4, 5, failures.Count == beforeSection4,
            "DevicePage.xaml and DeviceViewModel.cs both still show the expected wiring.",
            "one or more crude wiring checks failed (see above).",
            "Crude UI/VM wiring checks failed");
    }

    // ---------------------------------------------------------------------------------------
    // 5. M12c-amend Part C: CompanionInstallGate.Decide's truth table (the REAL function, not a
    //    model of it — GUIDE.md 4.1) — already-installed / wizard-active / declined-this-session
    //    each override "ask", and only the true fresh-non-wizard-connect case asks. Plus a crude
    //    source-text check that the non-wizard path can't reach the install call without going
    //    through the gate first (C3.1's "cannot reach the install call without passing the gate").
    // ---------------------------------------------------------------------------------------
    Console.WriteLine("\n[5/5] CompanionInstallGate.Decide truth table + gate wiring (source-text)...");
    var truthTable = new (bool Installed, bool WizardActive, bool DeclinedThisSession, CompanionInstallDecision Expected, string Label)[]
    {
        (true, false, false, CompanionInstallDecision.Skip, "already installed -> skip, no matter what else is true"),
        (true, true, false, CompanionInstallDecision.Skip, "already installed even mid-wizard -> skip"),
        (true, false, true, CompanionInstallDecision.Skip, "already installed and previously declined -> skip"),
        (false, true, false, CompanionInstallDecision.InstallSilently, "wizard active, not installed -> install silently, no prompt"),
        (false, true, true, CompanionInstallDecision.InstallSilently, "wizard active wins even over a prior decline"),
        (false, false, true, CompanionInstallDecision.Skip, "declined this session, non-wizard -> skip, never re-ask"),
        (false, false, false, CompanionInstallDecision.Ask, "fresh non-wizard connect, not installed -> ask"),
    };
    var t = 1;
    foreach (var row in truthTable)
    {
        var actual = CompanionInstallGate.Decide(row.Installed, row.WizardActive, row.DeclinedThisSession);
        CheckSub(t++, truthTable.Length, actual == row.Expected,
            $"{row.Label}: Decide({row.Installed}, {row.WizardActive}, {row.DeclinedThisSession}) = {actual}.",
            $"{row.Label}: Decide({row.Installed}, {row.WizardActive}, {row.DeclinedThisSession}) = {actual}, expected {row.Expected}.",
            $"CompanionInstallGate.Decide wrong for [{row.Label}]");
    }

    var setupPath = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "Services", "PhoneSetupService.cs");
    var beforeSection5 = failures.Count;
    if (!File.Exists(setupPath))
    {
        Check(5, 5, false, "", $"cannot read {setupPath}", $"Missing file: {setupPath}");
    }
    else
    {
        var setupSrc = File.ReadAllText(setupPath);
        var ensureReadyStart = setupSrc.IndexOf("public async Task<bool> EnsureReadyAsync", StringComparison.Ordinal);
        var ensureReadyEnd = ensureReadyStart >= 0 ? setupSrc.IndexOf("\n    public ", ensureReadyStart + 1, StringComparison.Ordinal) : -1;
        var ensureReadyBody = ensureReadyStart >= 0 && ensureReadyEnd > ensureReadyStart
            ? setupSrc[ensureReadyStart..ensureReadyEnd]
            : "";

        CheckSub(1, 3, ensureReadyBody.Length > 0 && ensureReadyBody.Contains("CompanionInstallGate.Decide(", StringComparison.Ordinal),
            "EnsureReadyAsync calls CompanionInstallGate.Decide(...) — the non-wizard path cannot skip the gate.",
            "EnsureReadyAsync no longer calls CompanionInstallGate.Decide(...) — a non-wizard connect could install without asking again.",
            "EnsureReadyAsync no longer calls the gate");

        var installCallCount = System.Text.RegularExpressions.Regex.Matches(setupSrc, @"InstallCompanionAsync\(device, ct\)").Count;
        CheckSub(2, 3, installCallCount == 2,
            $"InstallCompanionAsync(device, ct) is called from exactly 2 places (EnsureReadyAsync's gated branch, ConfirmInstallAsync's explicit-consent branch); found {installCallCount}.",
            $"InstallCompanionAsync(device, ct) is called from {installCallCount} place(s), expected exactly 2 — a new caller may have bypassed the gate (or a guarded one was removed).",
            "InstallCompanionAsync call-site count changed — gate may be bypassed");

        CheckSub(3, 3, ensureReadyBody.Contains("case CompanionInstallDecision.Ask:", StringComparison.Ordinal) &&
                       ensureReadyBody.IndexOf("CompanionInstallGate.Decide(", StringComparison.Ordinal) <
                       ensureReadyBody.IndexOf("InstallCompanionAsync(device, ct)", StringComparison.Ordinal),
            "EnsureReadyAsync's Ask case exists and the gate decision textually precedes the install call.",
            "EnsureReadyAsync's Ask case is missing, or the install call now precedes the gate decision in source order.",
            "EnsureReadyAsync gate ordering broken");

        Check(5, 5, failures.Count == beforeSection5,
            "the truth table matches CompanionInstallGate.Decide and PhoneSetupService.cs still routes installs through it.",
            "one or more gate checks failed (see above).",
            "CompanionInstallGate wiring checks failed");
    }
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup of a Temp scratch dir */ }
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

static string FindRepoRoot(string start)
{
    // Same posture as storesim's FindRepoRoot: walk up from CWD, and at each ancestor check
    // both <ancestor>\DESKTOP\... (CWD already Linc\) and <ancestor>\Linc/DESKTOP/... (CWD is
    // the yellow\ workspace root), so the harness resolves correctly from either launch point.
    static string? Marker(string dir) =>
        File.Exists(Path.Combine(dir, "DESKTOP", "Linc.Desktop", "Services", "ApkInstall.cs"))
            ? dir
            : File.Exists(Path.Combine(dir, "Linc", "DESKTOP", "Linc.Desktop", "Services", "ApkInstall.cs"))
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
