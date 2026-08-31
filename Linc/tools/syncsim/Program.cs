using Linc.Desktop.Services;

// Scripted round-trip against the REAL SyncEngine (D-027 / ROADMAP M18c). Runs every scenario
// twice: once with ADB semantics (push auto-renames, delete works) and once with Direct TLS
// semantics (push overwrites in place, delete throws) — the transports must behave identically.
//
//   dotnet run --project tools/syncsim
//
// A0 (M12g-amend): SyncEngine's sync-state.json now lives under the injected
// IDeviceRegistry.RootPath, and each Harness below points that at its own temp root — so this
// run never touches the owner's real %LOCALAPPDATA%\Linc/sync-state.json and there is nothing
// to back up or restore.

var failures = new List<string>();
foreach (var (label, overwriteOnPush, supportsDelete) in
         new[] { ("ADB sync", false, true), ("Direct TLS", true, false) })
{
    Console.WriteLine($"=== transport: {label} ===");
    await RunScenarios(label, overwriteOnPush, supportsDelete, failures);
    Console.WriteLine();
}

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("ALL SCENARIOS PASSED");
    return 0;
}
Console.WriteLine($"{failures.Count} FAILURE(S):");
foreach (var f in failures) Console.WriteLine($"  - {f}");
return 1;

static async Task RunScenarios(string label, bool overwriteOnPush, bool supportsDelete, List<string> failures)
{
    var scenarios = new (string Name, Func<Harness, Task> Body)[]
    {
        ("new file both ways", async h =>
        {
            h.WritePc("frompc.txt", "pc original", minutesAgo: 10);
            h.WritePhone("fromphone.txt", "phone original", minutesAgo: 10);
            await h.Reconcile();
            h.Expect(h.PhoneText("frompc.txt") == "pc original", "PC file reached the phone");
            h.Expect(h.PcText("fromphone.txt") == "phone original", "phone file reached the PC");
        }),

        ("re-sync is a no-op (no duplicates)", async h =>
        {
            h.WritePc("stable.txt", "same", minutesAgo: 10);
            await h.Reconcile();
            await h.Reconcile();
            await h.Reconcile();
            h.Expect(h.PhoneNames().Count(n => n.StartsWith("stable")) == 1, "no '(2)' duplicate on the phone");
            h.Expect(h.PcNames().Count(n => n.StartsWith("stable")) == 1, "no '(2)' duplicate on the PC");
        }),

        ("newest-wins phone -> PC", async h =>
        {
            h.WritePc("conflict.txt", "old pc copy", minutesAgo: 30);
            h.WritePhone("conflict.txt", "new phone copy", minutesAgo: 1);
            await h.Reconcile();
            h.Expect(h.PcText("conflict.txt") == "new phone copy", "newer phone copy won");
            await h.Reconcile();
            h.Expect(h.PcText("conflict.txt") == "new phone copy", "stable on the next pass");
            h.Expect(h.PhoneText("conflict.txt") == "new phone copy", "phone side unchanged");
        }),

        // The regression that mattered: overwriting toward the phone used to call DeleteAsync
        // unconditionally, which throws on a delete-less transport.
        ("newest-wins PC -> phone", async h =>
        {
            h.WritePhone("conflict2.txt", "old phone copy", minutesAgo: 30);
            h.WritePc("conflict2.txt", "new pc copy", minutesAgo: 1);
            await h.Reconcile();
            h.Expect(h.PhoneText("conflict2.txt") == "new pc copy", "newer PC copy won");
            h.Expect(h.PhoneNames().Count(n => n.StartsWith("conflict2")) == 1, "overwrote instead of duplicating");
            await h.Reconcile();
            h.Expect(h.PhoneText("conflict2.txt") == "new pc copy", "stable on the next pass");
        }),

        // The M00 field bug: one unreadable file aborted the whole pass before the seen-state
        // was written, so the next launch re-copied every file in the folder.
        ("one bad file does not lose everyone's bookkeeping", async h =>
        {
            h.WritePhone("good1.txt", "a", minutesAgo: 10);
            h.WritePhone("bad.txt", "b", minutesAgo: 10);
            h.WritePhone("good2.txt", "c", minutesAgo: 10);
            h.FailPullFor("bad.txt");
            await h.Reconcile();

            h.Expect(h.PcText("good1.txt") == "a", "the readable files still arrived");
            h.Expect(h.PcText("good2.txt") == "c", "a later file was not skipped by the failure");
            h.Expect(!h.PcExists("bad.txt"), "the failing file did not arrive");

            var state = h.PersistedState();
            h.Expect(state.Contains("good1.txt"), "seen-state was persisted despite the failure");
            h.Expect(state.Contains("good2.txt"), "seen-state covers files copied after the failure");
            h.Expect(!state.Contains("bad.txt"), "the failed file is left unrecorded so it retries");
        }),

        ("delete does not propagate or resurrect", async h =>
        {
            h.WritePc("gone.txt", "doomed", minutesAgo: 10);
            await h.Reconcile();
            h.Expect(h.PhoneExists("gone.txt"), "file synced to the phone first");
            File.Delete(Path.Combine(h.PcDir, "gone.txt"));
            await h.Reconcile();
            h.Expect(h.PhoneExists("gone.txt"), "phone copy NOT deleted (no delete propagation)");
            await h.Reconcile();
            h.Expect(!h.PcExists("gone.txt"), "deleted PC copy NOT resurrected");
        }),
    };

    foreach (var (name, body) in scenarios)
    {
        using var harness = new Harness(overwriteOnPush, supportsDelete);
        try
        {
            await body(harness);
            var bad = harness.Failures;
            if (bad.Count == 0)
            {
                Console.WriteLine($"  PASS  {name}");
            }
            else
            {
                Console.WriteLine($"  FAIL  {name}");
                foreach (var b in bad)
                {
                    Console.WriteLine($"          {b}");
                    failures.Add($"[{label}] {name}: {b}");
                }
                // The engine's own account of the pass, so a failure says WHAT it did, not only
                // that the outcome was wrong.
                Console.WriteLine($"          -- engine log ({harness.PhoneListCount} passes listed the folders):");
                foreach (var line in harness.LogLines)
                {
                    Console.WriteLine($"             {line}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  {name} — threw {ex.GetType().Name}: {ex.Message}");
            failures.Add($"[{label}] {name}: threw {ex.GetType().Name}: {ex.Message}");
        }
    }
}

sealed class Harness : IDisposable
{
    private readonly string _root;
    private readonly FakePhoneFiles _phone;
    private readonly FakeLog _log = new();
    private readonly string _statePath;
    private readonly FakeRegistry _registry;

    /// <summary>
    /// Created on the FIRST <see cref="Reconcile"/>, never in this constructor — see the note
    /// there. Null until a scenario has finished writing its fixtures.
    /// </summary>
    private SyncEngine? _engine;

    /// <summary>Set by the first <see cref="Reconcile"/>, so its ordering check runs exactly once.</summary>
    private bool _firstReconcileDone;
    public string PcDir { get; }
    public string PhoneRoot { get; }
    public List<string> Failures { get; } = [];

    private const string PhonePath = "/sdcard/Download";

    public Harness(bool overwriteOnPush, bool supportsDelete)
    {
        _root = Path.Combine(Path.GetTempPath(), "linc-syncsim", Guid.NewGuid().ToString("N")[..8]);
        PcDir = Path.Combine(_root, "pc");
        PhoneRoot = Path.Combine(_root, "phone");
        var storeRoot = Path.Combine(_root, "store");
        Directory.CreateDirectory(PcDir);
        Directory.CreateDirectory(Path.Combine(PhoneRoot, "sdcard", "Download"));
        Directory.CreateDirectory(storeRoot);

        // A0 (M12g-amend): SyncEngine now takes its sync-state.json root from
        // IDeviceRegistry.RootPath instead of Environment.GetFolderPath directly, so each
        // scenario's clean seen-state lives under this run's own temp root — never the owner's
        // real %LOCALAPPDATA%\Linc/sync-state.json (this harness used to delete that file at the
        // top of every scenario; D-057's temp-root rule now covers it too).
        _statePath = Path.Combine(storeRoot, "sync-state.json");

        _phone = new FakePhoneFiles(PhoneRoot, overwriteOnPush, supportsDelete);
        _registry = new FakeRegistry(PcDir, PhonePath, storeRoot);

        // M13e §1.3 — THE ENGINE IS DELIBERATELY NOT CONSTRUCTED HERE.
        //
        // SyncEngine's constructor calls Reconfigure(), which Kick()s a pass immediately on the
        // thread pool. Every scenario below then writes its fixture files, so that pass ran
        // CONCURRENTLY with the setup it was supposed to be testing. In the window between the
        // first WritePc and the following WritePhone the engine saw a PC file and an empty phone
        // folder, correctly decided to push, and wrote the OLD PC copy over the phone's newer
        // one — after which the seen-state recorded both sides in sync and no later pass ever
        // pulled. The engine's log for a failing run is one line: "Synced conflict.txt to the
        // phone". That is right behaviour on the world it was shown; the harness simply showed
        // it a half-built one.
        //
        // The scenarios also backdate every fixture's mtime, which defeats SyncEngine's
        // SettleSeconds guard — the 2 s rule that in production stops it acting on a file still
        // being written. So the harness disabled the protection and then raced it.
        //
        // Construction therefore waits for the first Reconcile(), by which time the fixtures are
        // on disk. This is an ordering guarantee, not a sleep (M4b's lesson from cleanroomsim's
        // fixed 5 s wait), and PhoneListCount below asserts it rather than assuming it.
    }

    private SyncEngine Engine => _engine ??= new SyncEngine(_phone, new FakeSupervisor(), _registry, _log);

    public void FailPullFor(string name) => _phone.FailPullFor.Add(name);

    /// <summary>
    /// What the REAL engine said it did, in order. Printed whenever a scenario fails: without it
    /// a failure reports only which claim broke, and two sessions were spent guessing at whether
    /// a wrong outcome came from a pull, a push, or no pass at all.
    /// </summary>
    public IReadOnlyList<string> LogLines => _log.Lines;

    /// <summary>How many reconcile passes have looked at the folders (see FakePhoneFiles.ListCount).</summary>
    public int PhoneListCount => Volatile.Read(ref _phone.ListCount);

    /// <summary>The seen-state as actually written to disk — empty string when never persisted.</summary>
    public string PersistedState() => File.Exists(_statePath) ? File.ReadAllText(_statePath) : "";

    /// <summary>How long <see cref="Reconcile"/> will wait for the engine to go quiet before it
    /// fails loudly — cleanroomsim's number (M4b Part 0), reused so both harnesses agree.</summary>
    private static readonly TimeSpan SettleCeiling = TimeSpan.FromSeconds(30);

    /// <summary>Gap between polls when a call found the gate already held.</summary>
    private const int PollMs = 100;

    /// <summary>How many consecutive no-change passes count as "quiet".</summary>
    private const int QuietPassesRequired = 2;

    /// <summary>
    /// Drives passes until the engine goes quiet. A single ReconcileNowAsync can return
    /// immediately when another pass already holds the gate (the constructor kicks one, and the
    /// PC file-watcher kicks more), so one call is not a barrier — loop until settled.
    ///
    /// M4c Part 0: this used to run six passes with a fixed <c>await Task.Delay(120)</c> between
    /// them — a wait standing in for a condition, which is the shape of bug M4b cured in
    /// cleanroomsim. It now polls for the condition itself: keep running passes until two
    /// consecutive ones leave the observable world (<see cref="WorldSnapshot"/>) untouched, or
    /// the <see cref="SettleCeiling"/> elapses.
    ///
    /// The quiet test is deliberately the WORLD only, not the engine's log: the "one bad file"
    /// scenario leaves a permanently unsyncable file, which by design is left unrecorded so it
    /// retries — so it logs a fresh warning on every pass, forever. Requiring a quiet log there
    /// could never be satisfied (measured: ~10,300 passes in 30 s, all warning about bad.txt).
    ///
    /// A call that returns without the phone folder having been listed did not run a pass at all
    /// — SyncEngine.ReconcileNowAsync takes the gate with <c>WaitAsync(0)</c> and returns early
    /// when another pass holds it. That call observed nothing, so it is not evidence of quiet;
    /// the loop waits <see cref="PollMs"/> and asks again rather than counting it.
    ///
    /// On timeout it records a failure naming what was and was not true. A harness that silently
    /// gives up is worse than one that flakes: the scenario's own assertions would then run
    /// against a half-settled world and blame the engine.
    /// </summary>
    public async Task Reconcile()
    {
        if (!_firstReconcileDone)
        {
            // The guarantee the fix rests on, asserted rather than assumed (GUIDE.md §4.2): at
            // the moment a scenario asks for its FIRST pass, no pass may have happened yet.
            // The flag is separate from _engine on purpose — keying this off "_engine is null"
            // would make the check silently skip itself the moment someone put construction back
            // in the constructor, which is precisely the change it exists to catch.
            _firstReconcileDone = true;
            Expect(_engine is null, "the engine had not started before the fixtures were written");
            Expect(PhoneListCount == 0,
                $"no reconcile pass looked at the folders before the fixtures were written (saw {PhoneListCount})");
        }
        var engine = Engine; // first call constructs it, now that the fixtures exist

        var deadline = DateTime.UtcNow + SettleCeiling;
        var world = WorldSnapshot();
        var quietPasses = 0;
        var passesRun = 0;
        var gateHeldCalls = 0;

        while (quietPasses < QuietPassesRequired && DateTime.UtcNow < deadline)
        {
            var listsBefore = PhoneListCount;

            await engine.ReconcileNowAsync();

            if (PhoneListCount == listsBefore)
            {
                gateHeldCalls++;
            }
            else
            {
                passesRun++;
                var after = WorldSnapshot();
                if (after == world)
                {
                    quietPasses++;
                }
                else
                {
                    quietPasses = 0;
                    world = after;
                }
            }

            // The gap is what makes this a poll: a pass kicked by the PC file-watcher runs on
            // another thread, so a no-change reading taken the instant a pass returns can be
            // premature. Waiting here and reading again is the check, not the wait.
            await Task.Delay(PollMs);
        }

        if (quietPasses < QuietPassesRequired)
        {
            Failures.Add(
                $"the engine went quiet within {SettleCeiling.TotalSeconds:0}s — it did NOT. " +
                $"True: {passesRun} pass(es) ran and listed the folders; {gateHeldCalls} call(s) " +
                $"found the gate already held; {PhoneListCount} listing(s) total; " +
                $"{LogLines.Count} engine log line(s). " +
                $"Not true: {quietPasses} of {QuietPassesRequired} consecutive no-change passes. " +
                $"Last observed world: {world.Replace("\n", " | ")}");
        }
    }

    /// <summary>
    /// Everything a scenario can assert on — both folders' file names, lengths and contents, plus
    /// the persisted seen-state. Two consecutive passes that leave this string identical (and add
    /// no engine log line) is the condition <see cref="Reconcile"/> polls for. Compared as text so
    /// a failure can print the world it gave up on.
    /// </summary>
    private string WorldSnapshot()
    {
        static IEnumerable<string> Describe(string side, string dir) =>
            Directory.GetFiles(dir).OrderBy(p => p, StringComparer.Ordinal).Select(p =>
            {
                // A pass may be mid-copy on another thread; an unreadable file is a real state and
                // simply differs from the settled one, so the poll keeps going instead of throwing.
                var text = TryReadAllText(p);
                return $"{side}:{Path.GetFileName(p)}|{text?.Length.ToString() ?? "?"}|{text ?? "<in-flight>"}";
            });

        return string.Join("\n", Describe("pc", PcDir).Concat(Describe("phone", PhoneDir)))
            + "\nstate:" + (TryReadAllText(_statePath) ?? "<in-flight>");
    }

    private static string? TryReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Expect(bool condition, string what)
    {
        if (!condition) Failures.Add(what);
    }

    // Files are written with backdated mtimes: the engine deliberately skips PC files touched
    // in the last 2 seconds (still being written).
    public void WritePc(string name, string text, int minutesAgo)
        => Write(Path.Combine(PcDir, name), text, minutesAgo);

    public void WritePhone(string name, string text, int minutesAgo)
        => Write(Path.Combine(PhoneRoot, "sdcard", "Download", name), text, minutesAgo);

    private static void Write(string path, string text, int minutesAgo)
    {
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesAgo));
    }

    private string PhoneDir => Path.Combine(PhoneRoot, "sdcard", "Download");

    public bool PcExists(string name) => File.Exists(Path.Combine(PcDir, name));
    public bool PhoneExists(string name) => File.Exists(Path.Combine(PhoneDir, name));
    public string PcText(string name) => PcExists(name) ? File.ReadAllText(Path.Combine(PcDir, name)) : "<missing>";
    public string PhoneText(string name) => PhoneExists(name) ? File.ReadAllText(Path.Combine(PhoneDir, name)) : "<missing>";
    public IEnumerable<string> PcNames() => Directory.GetFiles(PcDir).Select(Path.GetFileName)!;
    public IEnumerable<string> PhoneNames() => Directory.GetFiles(PhoneDir).Select(Path.GetFileName)!;

    public void Dispose()
    {
        _engine?.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir; best effort */ }
    }
}
