using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Linc.Probe;

// Desktop-emulating ADB probe (D-031). Exercises every lane the real desktop drives, so a
// milestone can self-verify without the user touching the phone.
//
//   dotnet run --project tools/probe            all read-only checks
//   dotnet run --project tools/probe -- --write  also does a files push/pull round-trip
//
// Deliberately never probed: sms.send and call.dial (they would text/ring a real contact).

var serial = Environment.GetEnvironmentVariable("LINC_SERIAL") ?? "<your-device-serial>";
var allowWrites = args.Contains("--write");
var results = new List<(string Name, bool Pass, string Detail)>();

// --send-pc-media: emulate the desktop pushing v13 `pc.media.state`, then hold the control
// connection open so the phone's Home widget can be inspected with an ADB screenshot.
// Isolates the phone half of the PC-media feature from the desktop's GSMTC half.
if (args.Contains("--send-pc-media"))
{
    using var pushLink = await Link.OpenAsync(serial, maxVersion: 13);
    Console.WriteLine($"negotiated v{pushLink.NegotiatedVersion}");
    await pushLink.SendAsync("pc.media.state", new JsonObject
    {
        ["title"] = "Probe Test Track",
        ["artist"] = "Linc Probe",
        ["playing"] = true,
        ["positionMs"] = 30_000,
        ["durationMs"] = 210_000,
        ["app"] = "linc-probe",
    });
    Console.WriteLine("sent pc.media.state; holding the link open for 40s");
    await Task.Delay(TimeSpan.FromSeconds(40));
    return 0;
}

void Record(string name, bool pass, string detail)
{
    results.Add((name, pass, detail));
    Console.WriteLine($"  {(pass ? "PASS" : "FAIL")}  {name,-28} {detail}");
}

Console.WriteLine($"Linc probe — device {serial}");
Console.WriteLine(Link.RunAdb("devices", null));
Console.WriteLine();

using var link = await Link.OpenAsync(serial, maxVersion: 13);
Console.WriteLine($"Handshake: negotiated v{link.NegotiatedVersion}, phone app {link.PhoneAppVersion}, " +
                  $"sessionToken {(link.SessionToken is null ? "none" : "issued")}");
Console.WriteLine();

Record("handshake", link.NegotiatedVersion > 0, $"v{link.NegotiatedVersion}");
Record("session token (v>=5)", link.SessionToken is not null, link.SessionToken is null ? "absent" : "issued");
Record("v13 PC->phone push gate", link.NegotiatedVersion >= 13,
    link.NegotiatedVersion >= 13
        ? "phone accepts pc.media.state / share.incoming"
        : $"NEGOTIATED v{link.NegotiatedVersion} — desktop suppresses all PC->phone pushes");

// ---- status -------------------------------------------------------------
try
{
    var status = (await link.RequestAsync("status.get", [])) ["payload"]!.AsObject();
    var battery = (int?)status["battery"];
    Record("status.get", battery is not null, $"battery {battery}%, charging {(bool?)status["charging"]}");
    Record("v3 stats", status["uptimeMillis"] is not null,
        $"ram {(long?)status["ramUsedBytes"] / 1_000_000}/{(long?)status["ramTotalBytes"] / 1_000_000} MB");
    Record("v4 theme palettes", status["themeLight"] is not null && status["themeDark"] is not null,
        status["themeLight"] is not null ? "light+dark present" : "absent");
    Record("v7 dndEnabled", status.ContainsKey("dndEnabled"), $"{status["dndEnabled"]}");
    Record("v8 soundMode", status["soundMode"] is not null, $"{status["soundMode"]}");
    Record("v8 wallpaperId", status["wallpaperId"] is not null,
        status["wallpaperId"] is not null ? "present (All-files grant held)" : "absent — no All-files grant");
}
catch (Exception ex) { Record("status.get", false, ex.Message); }

// ---- pub/sub + notification backlog -------------------------------------
try
{
    await link.RequestAsync("subscribe", new JsonObject { ["topic"] = "notifications" });
    await Task.Delay(1500);
    var backlog = link.Unsolicited.Count(f => (string?)f["type"] == "notification.posted");
    Record("v6 subscribe + backlog", true, $"{backlog} notification(s) pushed on subscribe");
}
catch (Exception ex) { Record("v6 subscribe + backlog", false, ex.Message); }

// ---- bulk channel (3) ----------------------------------------------------
try
{
    await using var bulk = await link.OpenChannelAsync(3);
    await Framing.WriteAsync(bulk, new JsonObject { ["kind"] = "appIcon", ["id"] = "app.linc.android" }.ToJsonString());
    var icon = await Framing.ReadBytesAsync(bulk);
    Record("v5 bulk channel", icon.Length > 0, $"appIcon {icon.Length} bytes");
}
catch (Exception ex) { Record("v5 bulk channel", false, ex.Message); }

// ---- photos --------------------------------------------------------------
string? firstPhotoId = null;
try
{
    var reply = (await link.RequestAsync("photos.recent", new JsonObject { ["limit"] = 5 }))["payload"]!.AsObject();
    var photos = reply["photos"]?.AsArray();
    firstPhotoId = photos?.Count > 0 ? (string?)photos[0]!["id"] : null;
    Record("v10 photos.recent", photos is not null, $"{photos?.Count ?? 0} photo(s)");
}
catch (Exception ex) { Record("v10 photos.recent", false, ex.Message); }

if (firstPhotoId is not null)
{
    try
    {
        await using var bulk = await link.OpenChannelAsync(3);
        await Framing.WriteAsync(bulk, new JsonObject { ["kind"] = "photo", ["id"] = firstPhotoId }.ToJsonString());
        var thumb = await Framing.ReadBytesAsync(bulk);
        Record("v10 photo thumbnail", thumb.Length > 0, $"{thumb.Length} bytes JPEG");
    }
    catch (Exception ex) { Record("v10 photo thumbnail", false, ex.Message); }
}

// ---- files channel (2) ---------------------------------------------------
try
{
    await using var files = await link.OpenChannelAsync(2);
    await Framing.WriteAsync(files, new JsonObject { ["op"] = "list", ["path"] = "/storage/emulated/0" }.ToJsonString());
    var header = JsonNode.Parse(await Framing.ReadAsync(files))!.AsObject();
    var entries = header["entries"]?.AsArray();
    Record("v10 files list", entries is not null, entries is not null
        ? $"{entries.Count} entries"
        : $"error: {header["error"]}");
}
catch (Exception ex) { Record("v10 files list", false, ex.Message); }

if (allowWrites)
{
    const string remote = "/storage/emulated/0/Download/linc-probe-roundtrip.bin";
    try
    {
        var payload = RandomNumberGenerator.GetBytes(64 * 1024);

        await using (var files = await link.OpenChannelAsync(2))
        {
            await Framing.WriteAsync(files, new JsonObject
            { ["op"] = "push", ["path"] = remote, ["size"] = payload.Length }.ToJsonString());
            await files.WriteAsync(payload);
            await files.FlushAsync();
            var ack = JsonNode.Parse(await Framing.ReadAsync(files))!.AsObject();
            if (ack["ok"] is null) throw new InvalidOperationException($"push rejected: {ack["error"]}");
        }

        await using (var files = await link.OpenChannelAsync(2))
        {
            await Framing.WriteAsync(files, new JsonObject { ["op"] = "pull", ["path"] = remote }.ToJsonString());
            var head = JsonNode.Parse(await Framing.ReadAsync(files))!.AsObject();
            var size = (int?)head["size"] ?? -1;
            var back = new byte[size];
            var read = 0;
            while (read < size) read += await files.ReadAsync(back.AsMemory(read, size - read));
            Record("v10 files push+pull", back.SequenceEqual(payload), $"{size} bytes, byte-exact");
        }
        Link.RunAdb($"shell rm -f {remote}", serial);
    }
    catch (Exception ex) { Record("v10 files push+pull", false, ex.Message); }
}

// ---- sync lanes ----------------------------------------------------------
try
{
    var reply = await link.RequestAsync("sync.config", new JsonObject
    { ["folders"] = true, ["photos"] = true, ["messages"] = true, ["calls"] = true });
    Record("v11 sync.config", (string?)reply["type"] == "ok", $"{reply["type"]}");
}
catch (Exception ex) { Record("v11 sync.config", false, ex.Message); }

try
{
    var reply = (await link.RequestAsync("sms.list", new JsonObject { ["limit"] = 5 }))["payload"]!.AsObject();
    var messages = reply["messages"]?.AsArray();
    Record("v11 sms.list", messages is not null,
        messages is not null ? $"{messages.Count} message(s)" : $"error: {reply["code"] ?? reply["error"]}");
}
catch (Exception ex) { Record("v11 sms.list", false, ex.Message); }

try
{
    var reply = (await link.RequestAsync("call.log", new JsonObject { ["limit"] = 5 }))["payload"]!.AsObject();
    var calls = reply["calls"]?.AsArray();
    Record("v12 call.log", calls is not null,
        calls is not null ? $"{calls.Count} call(s)" : $"error: {reply["code"] ?? reply["error"]}");
}
catch (Exception ex) { Record("v12 call.log", false, ex.Message); }

// ---- summary -------------------------------------------------------------
Console.WriteLine();
var passed = results.Count(r => r.Pass);
Console.WriteLine($"{passed}/{results.Count} checks passed");
foreach (var (name, pass, detail) in results.Where(r => !r.Pass))
{
    Console.WriteLine($"  FAILED: {name} — {detail}");
}
return results.All(r => r.Pass) ? 0 : 1;
