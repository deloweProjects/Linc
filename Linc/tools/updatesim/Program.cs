// Verification harness for M17b (auto-update). Links the REAL production files —
// Services\UpdateDecision.cs, UpdateManifest.cs and UpdateCheckService.cs — rather than modelling
// them (GUIDE 4.1). Nothing here touches the network, the owner's settings.json, or any real
// install path: the manifest is served by a stub HttpMessageHandler that counts requests, and the
// download fixture is a throwaway file under Path.GetTempPath() (GUIDE 4.3).
//
//   dotnet run --project tools/updatesim
//
// Check 1 is M17b Part A's decision table, the ten rows the task file specifies.
// Check 2 is M19 acceptance item 9(b): with "Keep Linc up to date" turned OFF the feature is
//   INERT — proven as a NUMBER (Requests == 0, LogLines == 0), not by reading the code. M17b
//   keyed this off an empty manifest URL; M19 keys it off the switch, because the URL is now a
//   build-time constant rather than a user setting.
// Check 3 is B3's gate: at most one check per 6 h, one request in flight at a time.
// Check 4 is Part C's refusal: a SHA256 that does not match the manifest must NOT yield a file.
// Check 5 is the malformed-manifest path: garbage JSON degrades to None, it does not throw.
// Check 6 is M19 C4: a manifest that is well-formed JSON but has GARBAGE IN EVERY FIELD still
//   yields None. That is what a typo in Releases/update.json actually produces, and it is the
//   case that could otherwise reach every installed copy.
// Check 7 is M19 C5: the manifest ACTUALLY COMMITTED at Releases/update.json, read off disk,
//   yields None against the shipped version — nothing prompts until the owner bumps it.
//
// Negative proofs this harness is designed to catch:
//   (a) making Forced respect skippedVersion -> row 5 flips to None -> check 1 fails.
//   (b) making Compare string-based          -> row 6 flips         -> check 1 fails.
//   (c) deleting the `if (!updatesEnabled())` gate in UpdateCheckService.CheckAsync
//                                            -> a request is issued -> check 2 fails.
//   (d) weakening UpdateDecision.TryParse to accept junk            -> check 6 fails.

using System.Net;
using Linc.Desktop.Services;

Console.WriteLine("=== Linc Auto-Update Verification Harness (updatesim) ===");

var failures = new List<string>();
const int Total = 7;

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

void Sub(bool ok, string text)
{
    Console.WriteLine($"        {(ok ? "PASS" : "FAIL")}: {text}");
}

// ---------------------------------------------------------------------------------------
// 1. The decision table (M17b Part A).
// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n[1/{Total}] The decision rule — every case from the task file...");

(string? Installed, string? Latest, string? Minimum, string? Skipped, UpdateAction Expect, string Why)[] rows =
[
    ("1.0.0",  "1.0.0",  "1.0.0", null,    UpdateAction.None,     "equal all round"),
    ("1.2.0",  "1.1.0",  "1.0.0", null,    UpdateAction.None,     "latest older than installed"),
    ("1.0.0",  "1.1.0",  "1.0.0", null,    UpdateAction.Optional, "normal upgrade"),
    ("1.0.0",  "1.1.0",  "1.0.0", "1.1.0", UpdateAction.None,     "skip honoured for Optional"),
    ("1.0.0",  "1.1.0",  "1.1.0", "1.1.0", UpdateAction.Forced,   "SKIP IGNORED — the load-bearing case"),
    ("1.9.0",  "1.10.0", "1.0.0", null,    UpdateAction.Optional, "numeric, not string, comparison"),
    ("1.0.0",  null,     null,    null,    UpdateAction.None,     "empty manifest"),
    ("1.0.0",  "x.y.z",  "x.y.z", null,    UpdateAction.None,     "garbage never forces"),
    (null,     "1.1.0",  "1.1.0", null,    UpdateAction.None,     "unknown installed version"),
    ("1.0.0",  "1.1.0",  "",      null,    UpdateAction.Optional, "missing minimum is not a floor"),
];

var tableOk = true;
foreach (var (installed, latest, minimum, skipped, expect, why) in rows)
{
    var actual = UpdateDecision.Decide(installed, latest, minimum, skipped);
    var ok = actual == expect;
    tableOk &= ok;
    Console.WriteLine(
        $"        {(ok ? "PASS" : "FAIL")}: installed={Show(installed),-7} latest={Show(latest),-7} " +
        $"min={Show(minimum),-7} skipped={Show(skipped),-7} -> expected {expect,-8} actual {actual,-8}  ({why})");
}

Check(1, tableOk,
    $"all {rows.Length} decision rows match, including skip-ignored-for-Forced and 1.10.0 > 1.9.0.",
    "the decision table does not match — see the FAIL rows above.",
    "UpdateDecision.Decide returned the wrong action for at least one row");

// ---------------------------------------------------------------------------------------
// 2. Acceptance item 4: INERT with no URL set.
// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n[2/{Total}] With the update switch OFF the feature does nothing at all...");

// The stub would serve a manifest that FORCES an update. With the switch off it must never be
// fetched at all, so "Requests == 0" is the assertion that carries this check.
var counting = new CountingHandler("""{"latest":"9.9.9","minimumSupported":"9.9.9"}""");
var inert = new UpdateCheckService(
    updatesEnabled: () => false,
    installedVersion: () => "1.0.0",
    skippedVersion: () => null,
    http: new HttpClient(counting));

var inertAction = await inert.CheckAsync();
// "Check now" must not be a way around the switch either.
var nowAction = await inert.CheckNowAsync();

Sub(inertAction == UpdateAction.None, $"an automatic check with the switch off returns {inertAction} without acting.");
Sub(nowAction == UpdateAction.None, $"a manual Check-now with the switch off returns {nowAction} without acting.");
Sub(counting.Requests == 0, $"the handler saw {counting.Requests} HTTP request(s) — expected 0.");
Sub(inert.LogLines == 0, $"the service emitted {inert.LogLines} log line(s) — expected 0.");

Check(2,
    inertAction == UpdateAction.None && nowAction == UpdateAction.None
        && counting.Requests == 0 && inert.LogLines == 0,
    "switch off => ZERO network calls, no action, no log line. The manifest that WOULD have forced an update was never fetched.",
    "the update feature acted despite the update switch being off.",
    "update feature is not inert with the update switch off");

// ---------------------------------------------------------------------------------------
// 3. B3's gate: one check per 6 h.
// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n[3/{Total}] The 6-hour gate and the single-in-flight rule...");

var gateHandler = new CountingHandler("""{"latest":"1.1.0","minimumSupported":"1.0.0"}""");
var gated = new UpdateCheckService(
    () => true, () => "1.0.0", () => null, new HttpClient(gateHandler));

var first = await gated.CheckAsync();
var second = await gated.CheckAsync();
var third = await gated.CheckAsync();

Sub(first == UpdateAction.Optional, $"the first check ran and returned {first}.");
Sub(second == UpdateAction.None && third == UpdateAction.None,
    $"the next two checks returned {second}/{third} without re-fetching.");
Sub(gateHandler.Requests == 1, $"exactly {gateHandler.Requests} request(s) issued across 3 calls — expected 1.");
Sub(UpdateCheckService.MinimumInterval == TimeSpan.FromHours(6),
    $"MinimumInterval is {UpdateCheckService.MinimumInterval.TotalHours} h.");

Check(3,
    first == UpdateAction.Optional && second == UpdateAction.None && gateHandler.Requests == 1
        && UpdateCheckService.MinimumInterval == TimeSpan.FromHours(6),
    "three back-to-back checks issued exactly one request; the interval is 6 h.",
    "the gate let more than one request through.",
    "the 6-hour / single-in-flight gate does not hold");

// ---------------------------------------------------------------------------------------
// 4. Part C: a SHA256 mismatch must refuse. Throwaway temp fixture only (GUIDE 4.3).
// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n[4/{Total}] A download whose SHA256 does not match the manifest is refused...");

var fixtureRoot = Path.Combine(Path.GetTempPath(), "Linc_updatesim_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixtureRoot);
try
{
    var payload = "not-a-real-installer"u8.ToArray();
    var payloadPath = Path.Combine(fixtureRoot, "payload.bin");
    File.WriteAllBytes(payloadPath, payload);
    var realHash = UpdateCheckService.Sha256OfFile(payloadPath);

    var downloader = new UpdateCheckService(
        () => true, () => "1.0.0", () => null,
        new HttpClient(new BytesHandler(payload)));

    // M19 C1's nested shape. The DESKTOP asset is the one this app downloads.
    static UpdateManifest Fixture(string? sha) => new(
        "1.1.0", "1.0.0", null,
        new UpdateAsset("https://example.invalid/Linc.zip", sha),
        new UpdateAsset("https://example.invalid/Linc.apk", sha));

    var good = await downloader.DownloadAndVerifyAsync(Fixture(realHash), fixtureRoot);
    var bad = await downloader.DownloadAndVerifyAsync(Fixture(new string('a', 64)), fixtureRoot);
    var none = await downloader.DownloadAndVerifyAsync(Fixture(null), fixtureRoot);

    Sub(good.Ok, $"a matching hash verifies: \"{good.Message}\"");
    Sub(!bad.Ok && bad.Path is null, $"a MISMATCHED hash is refused: \"{bad.Message}\"");
    Sub(!none.Ok, $"a manifest with NO checksum is refused rather than trusted: \"{none.Message}\"");
    Sub(!bad.Message.Contains("sha", StringComparison.OrdinalIgnoreCase),
        "the refusal message is plain language — no hash jargon reaches the user.");

    Check(4, good.Ok && !bad.Ok && bad.Path is null && !none.Ok,
        "a good hash verifies; a mismatched or absent hash is refused and no path is handed back.",
        "an unverified download was accepted.",
        "DownloadAndVerifyAsync accepted a file whose hash did not match");
}
finally
{
    try { Directory.Delete(fixtureRoot, recursive: true); } catch (IOException) { }
}

// ---------------------------------------------------------------------------------------
// 5. A malformed manifest degrades to None; it does not throw and never forces.
// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n[5/{Total}] A malformed or hostile manifest degrades to None...");

var garbage = new UpdateCheckService(
    () => true, () => "1.0.0", () => null,
    new HttpClient(new CountingHandler("this is not json {{{")));
var garbageAction = await garbage.CheckAsync();

var dead = new UpdateCheckService(
    () => true, () => "1.0.0", () => null,
    new HttpClient(new ThrowingHandler()));
var deadAction = await dead.CheckAsync();

Sub(UpdateManifest.TryParse("this is not json {{{") is null, "TryParse returns null for non-JSON.");
Sub(garbageAction == UpdateAction.None, $"a garbage manifest yields {garbageAction}.");
Sub(deadAction == UpdateAction.None, $"a backend that throws yields {deadAction} and does not propagate.");

Check(5, garbageAction == UpdateAction.None && deadAction == UpdateAction.None,
    "garbage JSON and a dead backend both degrade to None — a backend typo cannot brick the app.",
    "a malformed manifest or a dead backend did not degrade to None.",
    "malformed manifest handling does not degrade to None");

// ---------------------------------------------------------------------------------------
// 6. M19 C4: a WELL-FORMED manifest with garbage in EVERY field still yields None.
//
//    Check 5 covers text that is not JSON at all. This is the more dangerous case: valid JSON
//    of the right shape whose values are nonsense — exactly what a fat-fingered edit to
//    Releases/update.json produces. Since that file now reaches every installed copy, "None"
//    here is the property that stops a typo becoming a fleet-wide forced update.
// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n[6/{Total}] A well-formed manifest with garbage in every field yields None...");

const string GarbageJson = """
{
  "latest": "not-a-version",
  "minimumSupported": "@@@@",
  "notes": "",
  "desktop": { "url": "", "sha256": "zzzz" },
  "android": { "url": "nonsense", "sha256": "" }
}
""";

var junkHandler = new CountingHandler(GarbageJson);
var junk = new UpdateCheckService(() => true, () => "1.0.0", () => null, new HttpClient(junkHandler));
var junkAction = await junk.CheckAsync();

// Also drive the pure rule directly, so this does not depend on the fetch path at all.
var junkParsed = UpdateManifest.TryParse(GarbageJson);
var junkDecision = UpdateDecision.Decide("1.0.0", junkParsed?.Latest, junkParsed?.MinimumSupported, null);
// ...and the degenerate cases the file could also be left in.
var emptyObject = UpdateDecision.Decide("1.0.0", UpdateManifest.TryParse("{}")?.Latest,
    UpdateManifest.TryParse("{}")?.MinimumSupported, null);
var nullFields = UpdateDecision.Decide("1.0.0", null, null, null);

Sub(junkParsed is not null, "the garbage manifest IS valid JSON and does parse — this is not the check-5 case.");
// Tighter than "the answer came out None": assert the GUARD ITSELF rejects junk. Without this a
// lenient TryParse that silently substitutes 0 for an unparseable chunk still yields None here
// and the check would sail past a real weakening (GUIDE 4.4 — assertions must be able to fail).
var rejectsJunk = !UpdateDecision.TryParse("not-a-version", out _)
    && !UpdateDecision.TryParse("@@@@", out _)
    && !UpdateDecision.TryParse("1.x.0", out _)
    && !UpdateDecision.TryParse("", out _)
    && UpdateDecision.TryParse("1.0.0-beta.1", out _);
Sub(rejectsJunk, "TryParse REJECTS junk versions outright and still accepts a real one.");
Sub(junkAction == UpdateAction.None, $"fetched through the real service it yields {junkAction}.");
Sub(junkDecision == UpdateAction.None, $"the decision rule alone yields {junkDecision} for junk versions.");
Sub(emptyObject == UpdateAction.None, $"an empty JSON object yields {emptyObject}.");
Sub(nullFields == UpdateAction.None, $"all-null fields yield {nullFields}.");
Sub(junkHandler.Requests == 1, $"it really did go through the fetch path ({junkHandler.Requests} request).");

Check(6,
    junkParsed is not null && junkAction == UpdateAction.None && junkDecision == UpdateAction.None
        && emptyObject == UpdateAction.None && nullFields == UpdateAction.None && rejectsJunk,
    "garbage in every field yields None — never Forced. A typo in Releases/update.json cannot brick installed copies.",
    "a manifest full of garbage produced something other than None.",
    "a garbage-in-every-field manifest did not yield None");

// ---------------------------------------------------------------------------------------
// 7. M19 C5: the manifest THAT IS ACTUALLY COMMITTED yields None for the shipped version.
//
//    This reads Releases/update.json off disk rather than a fixture, so it fails if someone
//    commits a bump without meaning to. Seeded state = latest and minimumSupported both equal
//    the current version => nothing prompts anybody.
// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n[7/{Total}] The committed Releases/update.json yields None for the shipped version...");

// `dotnet run` keeps the caller's CWD, which may be Linc/ (the documented convention) or the
// repo root. Walk up for the manifest instead of assuming either, the same posture as
// packagesim's FindRepoRoot -- otherwise this check reports a missing file as a real failure.
var manifestPath = FindManifest(Directory.GetCurrentDirectory())
    ?? Path.GetFullPath(Path.Combine("..", "Releases", "update.json"));
var seededOk = false;
var seededAction = UpdateAction.Forced; // pessimistic default: only a real read may clear it

if (!File.Exists(manifestPath))
{
    Sub(false, $"Releases/update.json was not found at {manifestPath}.");
}
else
{
    var seededText = File.ReadAllText(manifestPath);
    var seeded = UpdateManifest.TryParse(seededText);
    if (seeded is null)
    {
        Sub(false, "the committed Releases/update.json did not parse.");
    }
    else
    {
        // The version both apps ship as. Desktop Assembly.Version is 1.0.0.0 and the Android
        // versionName is 1.0.0-beta.1; both parse to 1.0.0 (the suffix is ignored by design).
        const string Shipped = "1.0.0-beta.1";
        seededAction = UpdateDecision.Decide(Shipped, seeded.Latest, seeded.MinimumSupported, null);
        var numeric = UpdateDecision.Decide("1.0.0.0", seeded.Latest, seeded.MinimumSupported, null);

        Sub(seeded.Latest == Shipped, $"latest is \"{Show(seeded.Latest)}\".");
        Sub(seeded.MinimumSupported == Shipped, $"minimumSupported is \"{Show(seeded.MinimumSupported)}\".");
        Sub(seeded.Url is { Length: > 0 }, $"the desktop asset has a url: {Show(seeded.Url)}");
        Sub(seeded.Sha256 is { Length: 64 }, "the desktop asset has a 64-char sha256.");
        Sub(seeded.Android?.Url is { Length: > 0 }, "the android asset has a url.");
        Sub(seededAction == UpdateAction.None, $"the decision for the shipped version is {seededAction}.");
        Sub(numeric == UpdateAction.None, $"the decision for the numeric assembly version is {numeric}.");

        seededOk = seededAction == UpdateAction.None && numeric == UpdateAction.None
            && seeded.Latest == Shipped && seeded.MinimumSupported == Shipped
            && seeded.Sha256 is { Length: 64 };
    }
}

Check(7, seededOk,
    "the committed manifest is seeded to the shipped version — nothing prompts until the owner deliberately bumps it.",
    "the committed Releases/update.json would prompt or force an update on a current build.",
    "the seeded Releases/update.json does not yield None");

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

static string Show(string? value) => value is null ? "null" : value.Length == 0 ? "\"\"" : value;

/// <summary>Walks up from <paramref name="start"/> looking for Releases/update.json, so the harness
/// resolves the committed manifest whether it was launched from Linc/ or from the repo root.</summary>
static string? FindManifest(string start)
{
    for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "Releases", "update.json");
        if (File.Exists(candidate))
        {
            return candidate;
        }
    }
    return null;
}

// ---- stub transports: no socket is ever opened ----

/// <summary>Serves one fixed body and counts how many requests were actually issued.</summary>
internal sealed class CountingHandler(string body) : HttpMessageHandler
{
    public int Requests { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Requests++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body),
        });
    }
}

/// <summary>Serves fixed bytes, for the download-and-verify path.</summary>
internal sealed class BytesHandler(byte[] body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
}

/// <summary>A backend that is down.</summary>
internal sealed class ThrowingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
        throw new HttpRequestException("backend down");
}
