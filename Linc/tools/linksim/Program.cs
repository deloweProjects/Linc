using System.Text.Json;
using System.Text.RegularExpressions;
using Linc.Desktop.Services;

// D-081: the connection policy, tested pure, plus a reader that measures the connection targets
// from a real PC log.
//
//   dotnet run -c Release --project tools/linksim              every rule and scenario
//   dotnet run -c Release --project tools/linksim -- log [file] measure a PC log (default: today's)
//
// Nothing here opens a socket, runs adb, touches the phone or reads settings.json. The scenario
// model below composes the same LinkPolicy calls ConnectionSupervisor makes; it proves the rules
// produce the visible behaviour connection.md promises, not that the supervisor calls them (the
// build and the owner's test script cover that).

if (args.Length > 0 && args[0] == "log")
{
    return LogReport.Run(args.Length > 1 ? args[1] : null);
}

var failures = new List<string>();
void True(bool condition, string claim)
{
    Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {claim}");
    if (!condition) failures.Add(claim);
}

var t0 = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

Console.WriteLine("--- ranking");
True(LinkPolicy.Rank(LinkTransport.AdbUsb) > LinkPolicy.Rank(LinkTransport.AdbWireless), "USB outranks wireless ADB");
True(LinkPolicy.Rank(LinkTransport.AdbUsb) > LinkPolicy.Rank(LinkTransport.DirectTls), "USB outranks Direct TLS");
True(LinkPolicy.Rank(LinkTransport.AdbWireless) == LinkPolicy.Rank(LinkTransport.DirectTls), "the two Wi-Fi links rank the same");

Console.WriteLine("--- engaging");
var auto = ConnectionPreference.Auto;
True(LinkPolicy.ShouldEngage(LinkTransport.DirectTls, LinkActivity.Idle, null, null, auto), "idle: anything allowed engages");
True(LinkPolicy.ShouldEngage(LinkTransport.AdbUsb, LinkActivity.Connected, LinkTransport.DirectTls, null, auto), "on Wi-Fi: USB takes over");
True(!LinkPolicy.ShouldEngage(LinkTransport.AdbWireless, LinkActivity.Connected, LinkTransport.DirectTls, null, auto), "on Direct TLS: wireless ADB does NOT churn the link");
True(!LinkPolicy.ShouldEngage(LinkTransport.DirectTls, LinkActivity.Connected, LinkTransport.AdbWireless, null, auto), "on wireless ADB: Direct TLS does not either");
True(!LinkPolicy.ShouldEngage(LinkTransport.DirectTls, LinkActivity.Connected, LinkTransport.AdbUsb, null, auto), "on USB: nothing lower engages");
True(!LinkPolicy.ShouldEngage(LinkTransport.AdbUsb, LinkActivity.Connecting, null, null, auto), "never while a connect is in flight");
True(!LinkPolicy.ShouldEngage(LinkTransport.AdbUsb, LinkActivity.Paused, null, null, auto), "never while paused");
True(!LinkPolicy.ShouldEngage(LinkTransport.AdbUsb, LinkActivity.Idle, null, null, ConnectionPreference.WirelessOnly), "the phone's preference forbids");
True(!LinkPolicy.ShouldEngage(LinkTransport.AdbUsb, LinkActivity.Connected, LinkTransport.AdbWireless, LinkTransport.AdbWireless, auto), "a hand-picked transport is not taken over");
True(LinkPolicy.ShouldEngage(LinkTransport.AdbWireless, LinkActivity.Idle, null, LinkTransport.AdbWireless, auto), "...and is still engaged when idle");

Console.WriteLine("--- signals");
foreach (var strong in new[] { LinkSignal.CableIn, LinkSignal.CableOut, LinkSignal.NetworkChanged, LinkSignal.Resume, LinkSignal.BleSighting, LinkSignal.PhoneDialledIn, LinkSignal.UserAsked, LinkSignal.AppStart })
{
    True(LinkPolicy.IsStrong(strong), $"{strong} cuts backoff waits short");
}
True(!LinkPolicy.IsStrong(LinkSignal.ProbeFailing) && !LinkPolicy.IsStrong(LinkSignal.LinkDropped), "a probe hiccup or our own drop does not");
True(LinkPolicy.ShouldProbeLiveLink(LinkSignal.Resume) && LinkPolicy.ShouldProbeLiveLink(LinkSignal.NetworkChanged), "resume and network change probe a live link");
True(!LinkPolicy.ShouldProbeLiveLink(LinkSignal.BleSighting), "a BLE sighting doesn't");

Console.WriteLine("--- resume detection");
True(LinkPolicy.LooksLikeResume(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(5)), "a 5 s tick arriving after 2 min = the PC slept");
True(!LinkPolicy.LooksLikeResume(TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(5)), "a slightly late tick is not sleep");
True(!LinkPolicy.LooksLikeResume(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5)), "nor is a busy 20 s (4x is the threshold)");

Console.WriteLine("--- backoff");
var b = new EndpointBackoff();
const string ep = "192.168.1.61:5555";
True(!b.IsWaiting(ep, t0), "a fresh endpoint is tried at once");
True(b.Failed(ep, t0), "the first failure starts a streak");
True(b.IsWaiting(ep, t0.AddMilliseconds(500)) && !b.IsWaiting(ep, t0.AddSeconds(1)), "waits 1 s after one failure");
var waits = new List<int>();
var bb = new EndpointBackoff();
var now = t0;
for (var i = 0; i < 8; i++)
{
    bb.Failed(ep, now);
    var w = 0;
    while (bb.IsWaiting(ep, now.AddSeconds(w))) w++;
    waits.Add(w);
    now = now.AddSeconds(w);
}
True(waits.SequenceEqual(new[] { 1, 2, 5, 10, 20, 30, 30, 30 }), $"waits grow 1,2,5,10,20 and cap at 30 s (got {string.Join(",", waits)})");
bb.CutWaits();
True(!bb.IsWaiting(ep, now), "a strong signal cuts the wait short");
True(bb.Failures(ep) == 8 && bb.IsStale(ep), "...but keeps the failure count: an endpoint that kept failing is stale");
var fresh = new EndpointBackoff();
for (var i = 0; i < EndpointBackoff.ForgetAfter - 1; i++) fresh.Failed(ep, t0);
True(!fresh.IsStale(ep), $"{EndpointBackoff.ForgetAfter - 1} failures: still worth trying");
fresh.Failed(ep, t0);
True(fresh.IsStale(ep), $"{EndpointBackoff.ForgetAfter} failures in a row: forgotten");
fresh.Succeeded(ep);
True(fresh.Failures(ep) == 0 && !fresh.IsStale(ep), "a success wipes the streak");

Console.WriteLine("--- hotspot standby");
True(!HotspotBackoff.ShouldRetire(2, phoneLinked: true), "with the phone linked, a standby gets more than two chances");
True(HotspotBackoff.ShouldRetire(6, phoneLinked: true), "...and is retired after six");
True(HotspotBackoff.ShouldRetire(2, phoneLinked: false), "with no phone linked, two misses retire it (no 2.5-minute redial loop)");
True(!HotspotBackoff.ShouldRetire(1, phoneLinked: false), "...but one miss doesn't");

Console.WriteLine("--- log limiter");
var limiter = new LogLimiter();
var written = new List<string>();
for (var i = 0; i < 20; i++)
{
    if (limiter.Admit($"Health probe {i % 3 + 1}/3 failed ({i * 100} ms into the streak).", t0.AddSeconds(i)) is { } line) written.Add(line);
}
True(written.Count == LogLimiter.PerMinute, $"20 near-identical lines in 20 s: {LogLimiter.PerMinute} written (got {written.Count})");
var after = limiter.Admit("Health probe 1/3 failed (0 ms into the streak).", t0.AddSeconds(70));
True(after is not null && after.Contains("held back 17"), $"the next minute says how many were held back ({after})");
True(limiter.Admit("Something else entirely", t0.AddSeconds(5)) is not null, "different messages are counted separately");

Console.WriteLine("--- lost link");
True(LinkPolicy.OnLiveLinkLost(LinkSignal.CableOut, wifiPossible: true) == LostLink.HandOff, "cable out, Wi-Fi possible: hand off");
True(LinkPolicy.OnLiveLinkLost(LinkSignal.CableOut, wifiPossible: false) == LostLink.Drop, "cable out, USB only: a drop");
True(LinkPolicy.OnLiveLinkLost(LinkSignal.LinkDropped, wifiPossible: true) == LostLink.Drop, "a Wi-Fi link that died: a drop");
True(!LinkPolicy.AttemptIsVisible(LinkActivity.Connected, handingOff: false), "an attempt beside a live link is silent");
True(!LinkPolicy.AttemptIsVisible(LinkActivity.Connected, handingOff: true), "...so is one during a handoff");
True(LinkPolicy.AttemptIsVisible(LinkActivity.Idle, handingOff: false), "from nothing it shows Connecting");

Console.WriteLine("--- scenarios (what the user sees)");
{
    var m = new Model(t0);
    m.Appear(LinkTransport.DirectTls, ok: true);
    m.Appear(LinkTransport.AdbUsb, ok: true, after: 10);
    True(m.Transport == LinkTransport.AdbUsb && m.Visible.SequenceEqual(new[] { "Connecting", "Connected" }),
        $"cable in while on Wi-Fi: USB takes over with no drop ({m.Story})");
}
{
    var m = new Model(t0);
    m.Appear(LinkTransport.DirectTls, ok: true);
    m.Appear(LinkTransport.AdbUsb, ok: false, after: 10);
    True(m.Transport == LinkTransport.DirectTls && m.Visible.SequenceEqual(new[] { "Connecting", "Connected" }),
        $"a USB connect that fails beside Wi-Fi changes nothing ({m.Story})");
}
{
    var m = new Model(t0);
    m.Appear(LinkTransport.AdbUsb, ok: true);
    m.Lost(LinkSignal.CableOut, wifiPossible: true, after: 30);
    m.Appear(LinkTransport.DirectTls, ok: true, after: 2);   // the phone dials in at once
    m.Tick(after: 30);
    True(m.Transport == LinkTransport.DirectTls && m.Visible.SequenceEqual(new[] { "Connecting", "Connected" }),
        $"cable out with Wi-Fi: handed to Wi-Fi, no visible drop ({m.Story})");
}
{
    var m = new Model(t0);
    m.Appear(LinkTransport.AdbUsb, ok: true);
    m.Lost(LinkSignal.CableOut, wifiPossible: true, after: 30);
    m.Tick(after: 13);
    True(m.Visible.Last() == "Searching", $"cable out and no Wi-Fi link within the grace: an honest drop ({m.Story})");
}
{
    var m = new Model(t0);
    m.Appear(LinkTransport.AdbUsb, ok: true);
    m.Lost(LinkSignal.CableOut, wifiPossible: false, after: 30);
    True(m.Visible.Last() == "Searching", $"cable out, USB-only phone: dropped at once ({m.Story})");
}
{
    var m = new Model(t0);
    m.Appear(LinkTransport.AdbWireless, ok: true);
    m.Appear(LinkTransport.DirectTls, ok: true, after: 5);
    True(m.Transport == LinkTransport.AdbWireless && m.Visible.Count == 2, $"Wi-Fi never swaps for Wi-Fi ({m.Story})");
}

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("linksim: all checks green.");
    return 0;
}
Console.WriteLine($"linksim: {failures.Count} FAILURE(S)");
foreach (var f in failures) Console.WriteLine($"  {f}");
return 1;

/// <summary>
/// The supervisor's visible behaviour, composed from the same LinkPolicy calls it makes. A model,
/// stated as one: it shows the rules add up to "no visible drop", not that the app calls them.
/// </summary>
sealed class Model(DateTime start)
{
    private DateTime _now = start;
    private DateTime? _handoffUntil;
    public LinkActivity Activity { get; private set; } = LinkActivity.Idle;
    public LinkTransport? Transport { get; private set; }
    public List<string> Visible { get; } = [];
    public string Story => string.Join(" → ", Visible);

    private bool HandingOff => _handoffUntil is { } until && _now < until;

    private void Show(string what)
    {
        if (Visible.Count == 0 || Visible[^1] != what) Visible.Add(what);
    }

    public void Appear(LinkTransport transport, bool ok, int after = 0)
    {
        Tick(after);
        var activity = HandingOff ? LinkActivity.Idle : Activity;
        if (!LinkPolicy.ShouldEngage(transport, activity, HandingOff ? null : Transport, null, ConnectionPreference.Auto)) return;
        var visible = LinkPolicy.AttemptIsVisible(Activity, HandingOff);
        if (visible) Show("Connecting");
        if (ok)
        {
            Transport = transport;
            Activity = LinkActivity.Connected;
            _handoffUntil = null;
            Show("Connected");
        }
        else if (visible)
        {
            Activity = LinkActivity.Idle;
            Show("Searching");
        }
    }

    public void Lost(LinkSignal why, bool wifiPossible, int after = 0)
    {
        Tick(after);
        if (LinkPolicy.OnLiveLinkLost(why, wifiPossible) == LostLink.HandOff)
        {
            _handoffUntil = _now + LinkPolicy.HandoffGrace;
            return;
        }
        Drop();
    }

    public void Tick(int after = 0)
    {
        var was = HandingOff;
        _now = _now.AddSeconds(after);
        if (was && !HandingOff && _handoffUntil is not null) Drop();
    }

    private void Drop()
    {
        _handoffUntil = null;
        Transport = null;
        Activity = LinkActivity.Idle;
        Show("Searching");
    }
}

/// <summary>Measures connection.md §2.8's targets from a PC log (%LOCALAPPDATA%\Linc\logs).</summary>
static class LogReport
{
    private static readonly Regex Digits = new(@"\d+", RegexOptions.Compiled);

    public static int Run(string? path)
    {
        path ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Linc", "logs", $"linc-{DateTime.Now:yyyy-MM-dd}.ndjson");
        if (!File.Exists(path))
        {
            Console.WriteLine($"No log at {path}");
            return 1;
        }
        var rows = new List<(DateTimeOffset T, string M)>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                rows.Add((doc.RootElement.GetProperty("Timestamp").GetDateTimeOffset(),
                          doc.RootElement.GetProperty("Message").GetString() ?? ""));
            }
            catch (JsonException) { /* a torn last line while the app writes; skip it */ }
        }
        Console.WriteLine($"{path}: {rows.Count} lines, {rows.FirstOrDefault().T:HH:mm} – {rows.LastOrDefault().T:HH:mm}");

        Measure(rows, "Cable out → back on Wi-Fi", m => m.StartsWith("USB cable for"), target: 12,
            ok: m => m.StartsWith("Moved the link") || m.StartsWith("Connected to"));
        Measure(rows, "Cable in → on USB", m => m.Contains("is on the USB cable"), target: 3,
            ok: m => m.StartsWith("Moved the link to the USB cable") || m.EndsWith("(USB)"));
        Measure(rows, "PC wake → connected", m => m.StartsWith("PC woke from sleep"), target: 10,
            ok: m => m.StartsWith("Connected to") || m.StartsWith("Moved the link"),
            alreadyFine: m => m.Contains("checking the phone is still there"));
        Measure(rows, "Visible drop → connected", m => m.StartsWith("Connection dropped"), target: 5,
            ok: m => m.StartsWith("Connected to"));
        var visibleDrops = rows.Count(r => r.M.StartsWith("Connection dropped"));
        var handoffs = rows.Count(r => r.M.StartsWith("Moved the link"));
        Console.WriteLine($"Visible drops: {visibleDrops}; silent handoffs: {handoffs}");

        var worst = rows
            .GroupBy(r => (Minute: r.T.ToString("yyyy-MM-dd HH:mm"), Key: Digits.Replace(r.M, "#")))
            .Select(g => (g.Key.Key, g.Key.Minute, Count: g.Count()))
            .Where(g => g.Count > 3)
            .GroupBy(g => g.Key)
            .Select(g => g.OrderByDescending(x => x.Count).First())
            .OrderByDescending(g => g.Count)
            .Take(10)
            .ToList();
        Console.WriteLine(worst.Count == 0
            ? "No line repeated more than 3 times in any minute. (target met)"
            : "Lines repeated more than 3 times in a minute (target: none):");
        foreach (var (key, minute, count) in worst)
        {
            Console.WriteLine($"  {count,4}x at {minute}  {(key.Length > 100 ? key[..100] + "…" : key)}");
        }
        return 0;
    }

    private static void Measure(List<(DateTimeOffset T, string M)> rows, string name, Func<string, bool> start,
        double target, Func<string, bool> ok, Func<string, bool>? alreadyFine = null)
    {
        var times = new List<double>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (!start(rows[i].M)) continue;
            for (var j = i + 1; j < rows.Count && (rows[j].T - rows[i].T).TotalMinutes < 10; j++)
            {
                if (ok(rows[j].M))
                {
                    times.Add((rows[j].T - rows[i].T).TotalSeconds);
                    break;
                }
            }
        }
        if (times.Count == 0)
        {
            Console.WriteLine($"{name}: no events in this log");
            return;
        }
        times.Sort();
        var met = times.Count(t => t <= target);
        Console.WriteLine($"{name}: n={times.Count}, median {times[times.Count / 2]:0.0} s, worst {times[^1]:0.0} s; " +
            $"{met}/{times.Count} within the {target:0} s target");
    }
}
