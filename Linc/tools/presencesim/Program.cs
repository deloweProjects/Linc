using System.Text.RegularExpressions;

// Verification harness for M12 Part A (presence polish — "a silent catch is a bug factory").
// Audits the six connection-path files (ConnectionSupervisor, ConnectionManager, CompanionClient,
// DiscoveryService, UsbWatcherService, BlePresenceService) with two CRUDE source-text checks:
//
//   [1] Anti-silence guard: every `catch` block in those six files must contain either a call
//       that looks like a log call (`Log(`) or a `//` explanatory comment. A catch with neither
//       is exactly the "silent catch is a bug factory" pattern BRAIN.md warns about.
//   [2] No-body-in-log guard (A2.2): no `Log(...)` call in those six files may mention a
//       body/clipboard/content-shaped identifier — never log clipboard text, notification
//       bodies, message bodies, file contents, or TLS key material.
//
// Both are crude, deliberately: brace/paren-balanced text scans, not a real C# parser. They are
// tripwires that the guard identifier still exists in production, not a substitute for review —
// same posture as storesim's §11 no-body-in-log scan and displaysim's crude method-body checks.
// This harness touches no DeviceRegistry/LincStore and opens no real store (D-057 does not apply
// here — there is nothing stateful to protect).
//
//   dotnet run --project tools/presencesim

Console.WriteLine("=== Linc Presence Verification Harness (presencesim) ===");

var failures = new List<string>();
const int Total = 2;

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
var servicesDir = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "Services");

// The six files A2.1 names as the connection path (ConnectionSupervisor/ConnectionManager/
// CompanionClient/DiscoveryService/UsbWatcherService/BlePresenceService).
var targetFiles = new[]
{
    "ConnectionSupervisor.cs",
    "ConnectionManager.cs",
    "CompanionClient.cs",
    "DiscoveryService.cs",
    "UsbWatcherService.cs",
    "BlePresenceService.cs",
};

// ---------------------------------------------------------------------------------------
// 1. CRUDE anti-silence guard: every catch block in the six files must contain a `Log(` call
//    or a `//` comment. This IS the catch-block census — one line per catch block found.
// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n[1/{Total}] CRUDE: every catch block logs or explains its silence...");
var totalCatches = 0;
var silentCatches = 0;
try
{
    foreach (var file in targetFiles)
    {
        var path = Path.Combine(servicesDir, file);
        if (!File.Exists(path))
        {
            CheckSub(false, $"found {file}.", $"could not locate {path}; the crude scan cannot run.", $"{file} not found");
            continue;
        }
        var source = File.ReadAllText(path);
        var blocks = ExtractCatchBlocks(source);
        var fileSilent = 0;
        foreach (var (line, body) in blocks)
        {
            totalCatches++;
            var hasLog = body.Contains("Log(", StringComparison.Ordinal);
            var hasComment = body.Contains("//", StringComparison.Ordinal);
            var ok = hasLog || hasComment;
            if (!ok)
            {
                fileSilent++;
                silentCatches++;
            }
            Console.WriteLine(ok
                ? $"        PASS: {file}:{line} catch block {(hasLog ? "logs" : "is commented")}."
                : $"        FAIL: {file}:{line} catch block has neither a Log( call nor a // comment.");
        }
        if (fileSilent > 0)
        {
            failures.Add($"{file} has {fileSilent} silent, unexplained catch block(s)");
        }
        Console.WriteLine($"    -- {file}: {blocks.Count} catch block(s), {blocks.Count - fileSilent} compliant, {fileSilent} silent.");
    }
    Check(1, silentCatches == 0 && totalCatches > 0,
        $"all {totalCatches} catch blocks across the six files log or explain their silence.",
        $"{silentCatches} of {totalCatches} catch blocks are silent with no explanation (see FAILs above).",
        "Anti-silence guard found unexplained silent catch(es)");
}
catch (Exception ex)
{
    Console.WriteLine($"    [1/{Total}] FAIL: anti-silence scan threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"Anti-silence scan threw: {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 2. CRUDE no-body-in-log guard (A2.2): no Log(...) call in the six files may mention a
//    body/clipboard/content-shaped identifier. Style matches storesim §11's no-body-in-log scan:
//    intentionally weak (a string-token scan of the call text), a tripwire for the obvious case
//    per house style, not a substitute for review. Comments are excluded so a mention of these
//    words in an explanatory `//` line does not false-fire.
// ---------------------------------------------------------------------------------------
Console.WriteLine($"\n[2/{Total}] CRUDE: no Log( call mentions a body/clipboard/content-shaped identifier...");
var forbidden = new[] { "body", "clipboard", "content", "keymaterial", "key material", "tlskey", "messagetext" };
try
{
    foreach (var file in targetFiles)
    {
        var path = Path.Combine(servicesDir, file);
        if (!File.Exists(path))
        {
            CheckSub(false, $"found {file}.", $"could not locate {path}; the crude scan cannot run.", $"{file} not found (§2 scan)");
            continue;
        }
        var source = File.ReadAllText(path);
        var calls = ExtractLogCalls(source);
        var bad = calls.Where(c => forbidden.Any(f => c.Text.Contains(f, StringComparison.OrdinalIgnoreCase))).ToList();
        CheckSub(bad.Count == 0,
            $"{file}: scanned {calls.Count} Log( call(s), none mention a body/clipboard/content identifier.",
            $"{file}: {bad.Count} Log( call(s) mention a forbidden identifier — {string.Join(" | ", bad.Select(b => $"line {b.Line}: {b.Text}"))}.",
            $"{file} logs a body/clipboard/content-shaped identifier");
    }
    Check(2, !failures.Any(f => f.EndsWith("body/clipboard/content-shaped identifier", StringComparison.Ordinal)),
        "no Log( call across the six files mentions a body/clipboard/content-shaped identifier.",
        "at least one Log( call mentions a forbidden identifier (see FAILs above).",
        "No-body-in-log guard found a forbidden identifier");
}
catch (Exception ex)
{
    Console.WriteLine($"    [2/{Total}] FAIL: no-body-in-log scan threw: {ex.GetType().Name}: {ex.Message}");
    failures.Add($"No-body-in-log scan threw: {ex.GetType().Name}: {ex.Message}");
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

// ---- helpers ----

// CRUDE: finds `catch ... { ... }` clauses by regex + brace-balancing (not a real parser — it
// does not understand string/char literals, so a literal "{" inside a string could throw off the
// balance; none of the six files' catch bodies contain one today). Returns (1-based line number
// of the `catch` keyword, the block body text between the braces).
static List<(int Line, string Body)> ExtractCatchBlocks(string source)
{
    var text = source.Replace("\r\n", "\n");
    var results = new List<(int, string)>();
    var searchFrom = 0;
    var pattern = new Regex(@"catch\s*(\([^)]*\))?\s*(when\s*\([^)]*\))?\s*\{");
    while (true)
    {
        var m = pattern.Match(text, searchFrom);
        if (!m.Success)
        {
            break;
        }
        var braceIdx = m.Index + m.Length - 1; // position of the opening '{'
        var depth = 0;
        var i = braceIdx;
        for (; i < text.Length; i++)
        {
            if (text[i] == '{') { depth++; }
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0) { break; }
            }
        }
        var body = text[(braceIdx + 1)..Math.Min(i, text.Length)];
        var line = text[..m.Index].Count(c => c == '\n') + 1;
        results.Add((line, body));
        searchFrom = Math.Min(i + 1, text.Length);
    }
    return results;
}

// CRUDE: finds `Log(...)` call sites (matches both `log.Log(` and a bare `Log(`) by locating the
// literal "Log(" text and paren-balancing to the matching close, so a call wrapped across
// multiple lines (as several in ConnectionManager.cs are) is captured whole rather than truncated
// at the first line break. Returns (1-based line number, the full call text incl. "Log(...)").
static List<(int Line, string Text)> ExtractLogCalls(string source)
{
    var text = source.Replace("\r\n", "\n");
    var results = new List<(int, string)>();
    var idx = 0;
    while (true)
    {
        var hit = text.IndexOf("Log(", idx, StringComparison.Ordinal);
        if (hit < 0)
        {
            break;
        }
        // Skip a "Log(" that is itself inside a // comment on its line.
        var lineStart = text.LastIndexOf('\n', Math.Max(0, hit - 1)) + 1;
        var linePrefix = text[lineStart..hit];
        if (linePrefix.Contains("//", StringComparison.Ordinal))
        {
            idx = hit + 4;
            continue;
        }
        var parenIdx = hit + 3; // index of the opening '('
        var depth = 0;
        var i = parenIdx;
        for (; i < text.Length; i++)
        {
            if (text[i] == '(') { depth++; }
            else if (text[i] == ')')
            {
                depth--;
                if (depth == 0) { break; }
            }
        }
        var call = text[hit..Math.Min(i + 1, text.Length)];
        var line = text[..hit].Count(c => c == '\n') + 1;
        results.Add((line, call));
        idx = Math.Min(i + 1, text.Length);
    }
    return results;
}

static string FindRepoRoot(string start)
{
    // The harness runs from anywhere — `dotnet run` keeps the parent's CWD, which may be the
    // workspace root (yellow\) rather than the Linc\ code root. Walk up, and at each ancestor
    // check both <ancestor>\DESKTOP\... and <ancestor>\Linc/DESKTOP/... so both layouts resolve.
    static string? Marker(string dir) =>
        File.Exists(Path.Combine(dir, "DESKTOP", "Linc.Desktop", "Services", "ConnectionSupervisor.cs"))
            ? dir
            : File.Exists(Path.Combine(dir, "Linc", "DESKTOP", "Linc.Desktop", "Services", "ConnectionSupervisor.cs"))
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
