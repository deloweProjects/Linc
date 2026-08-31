using System.Text.Json;
using Linc.Desktop.Services;

// Multi-device semantics against the REAL DeviceRegistry (M03 / D-037).
//
//   dotnet run --project tools/devicesim
//
// The whole point of M03's registry change is that per-device settings CANNOT bleed between
// tabs, so that is what these scenarios assert — plus the pre-M03 migration, which runs once on
// a real user's settings file and is otherwise impossible to re-test by hand.
//
// D-057: every registry here is built against a throwaway temp root, so this harness cannot
// reach %LOCALAPPDATA%\Linc/settings.json by any path — not even if it is killed mid-run. A kill
// leaks an empty temp folder instead of destroying the owner's pairing. There is no backup to
// restore and no ritual for a future scenario to forget.

var root = Path.Combine(Path.GetTempPath(), "Linc_devicesim_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var settingsPath = Path.Combine(root, "settings.json");

var failures = new List<string>();
try
{
    foreach (var (name, body) in Scenarios(root))
    {
        Console.WriteLine($"--- {name}");
        if (File.Exists(settingsPath)) File.Delete(settingsPath);
        try
        {
            body(new Asserter(name, failures));
        }
        catch (Exception ex)
        {
            failures.Add($"{name}: threw {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"    THREW {ex.GetType().Name}: {ex.Message}");
        }
    }
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch (IOException) { /* a leaked temp dir is harmless */ }
    Console.WriteLine();
    Console.WriteLine($"(ran entirely under {root}; the real settings.json was never opened)");
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

static IEnumerable<(string Name, Action<Asserter> Body)> Scenarios(string root)
{
    yield return ("pairing a phone creates its tab and makes it active", a =>
    {
        var r = new DeviceRegistry(root);
        a.True(r.PairedSerial is null, "nothing paired on a clean start");
        a.True(r.KnownDevices.Count == 0, "no known devices on a clean start");

        r.SavePairedDevice("SERIAL-A", "Pixel 7");
        a.True(r.PairedSerial == "SERIAL-A", "the paired phone is active");
        a.True(r.KnownDevices.Count == 1, "it appears in the tab list");
        a.True(!r.KnownDevices[0].Hidden, "its tab is visible");
    });

    yield return ("per-device settings never bleed between devices", a =>
    {
        var r = new DeviceRegistry(root);
        r.SavePairedDevice("SERIAL-A", "Pixel 7");
        r.SaveLastHostPort("192.168.2.50:5555");
        r.SavePhoneCert("CERT-A");
        r.SaveSyncLane("messages", true);
        r.SaveFolderSyncPaths(@"C:\PhoneA", "/sdcard/A");

        r.SavePairedDevice("SERIAL-B", "Pixel 9");
        a.True(r.PairedSerial == "SERIAL-B", "the second phone became active");
        a.True(r.LastHostPort is null, "B has no address of its own yet");
        a.True(r.PhoneCertBase64 is null, "B has not pinned a certificate");
        a.True(!r.SyncLane("messages"), "B does not inherit A's Messages lane");
        a.True(r.FolderSyncPcPath is null, "B does not inherit A's folder pair");
        a.True(r.FolderSyncPhonePath == "/sdcard/Download", "B falls back to the default phone path");

        r.SaveLastHostPort("192.168.2.77:5555");
        r.SavePhoneCert("CERT-B");
        r.SaveSyncLane("calls", true);

        r.SetActiveDevice("SERIAL-A");
        a.True(r.LastHostPort == "192.168.2.50:5555", "A's address came back");
        a.True(r.PhoneCertBase64 == "CERT-A", "A's certificate came back");
        a.True(r.SyncLane("messages"), "A's Messages lane came back");
        a.True(!r.SyncLane("calls"), "A did not pick up B's Calls lane");
        a.True(r.FolderSyncPcPath == @"C:\PhoneA", "A's folder pair came back");
        a.True(r.FolderSyncPhonePath == "/sdcard/A", "A's phone path came back");

        r.SetActiveDevice("SERIAL-B");
        a.True(r.PhoneCertBase64 == "CERT-B", "B's certificate is still B's");
        a.True(r.SyncLane("calls"), "B's Calls lane survived the round trip");
        a.True(!r.SyncLane("messages"), "B still has no Messages lane");
    });

    yield return ("everything survives a restart", a =>
    {
        var first = new DeviceRegistry(root);
        first.SavePairedDevice("SERIAL-A", "Pixel 7");
        first.SaveLastHostPort("192.168.2.50:5555");
        first.SaveSyncLane("folders", true);
        first.SavePairedDevice("SERIAL-B", "Pixel 9");
        first.SavePhoneCert("CERT-B");
        first.SetActiveDevice("SERIAL-A");

        var reloaded = new DeviceRegistry(root);
        a.True(reloaded.PairedSerial == "SERIAL-A", "the active device persisted");
        a.True(reloaded.KnownDevices.Count == 2, "both devices persisted");
        a.True(reloaded.LastHostPort == "192.168.2.50:5555", "A's address persisted");
        a.True(reloaded.SyncLane("folders"), "A's Folders lane persisted");
        reloaded.SetActiveDevice("SERIAL-B");
        a.True(reloaded.PhoneCertBase64 == "CERT-B", "B's certificate persisted");
    });

    yield return ("switching raises ActiveDeviceChanged exactly once, and not on reconnect", a =>
    {
        var r = new DeviceRegistry(root);
        r.SavePairedDevice("SERIAL-A", "Pixel 7");
        r.SavePairedDevice("SERIAL-B", "Pixel 9");

        var switches = 0;
        r.ActiveDeviceChanged += () => switches++;

        r.SetActiveDevice("SERIAL-A");
        a.True(switches == 1, "picking another tab fired the switch");
        r.SetActiveDevice("SERIAL-A");
        a.True(switches == 1, "re-picking the same tab is a no-op");
        r.SetActiveDevice("SERIAL-UNKNOWN");
        a.True(switches == 1, "an unknown serial is a no-op");

        // Connecting to a phone must NOT raise it: the supervisor's handler tears the link down,
        // and it would be running inside the very connect that just succeeded (deadlock + drop).
        r.SavePairedDevice("SERIAL-B", "Pixel 9");
        a.True(switches == 1, "connecting to a different phone did not fire the switch event");
    });

    yield return ("closing a tab hides it; the pairing and its settings survive", a =>
    {
        var r = new DeviceRegistry(root);
        r.SavePairedDevice("SERIAL-A", "Pixel 7");
        r.SavePhoneCert("CERT-A");
        r.SavePairedDevice("SERIAL-B", "Pixel 9");

        r.SetDeviceHidden("SERIAL-A", hidden: true);
        a.True(r.KnownDevices.Count == 2, "a hidden device is still known");
        a.True(r.KnownDevices.Single(d => d.Serial == "SERIAL-A").Hidden, "its tab is hidden");

        r.SetActiveDevice("SERIAL-A");
        a.True(r.PhoneCertBase64 == "CERT-A", "the hidden device kept its certificate");

        // Reconnecting to a phone whose tab was closed brings the tab back.
        r.SavePairedDevice("SERIAL-A", "Pixel 7");
        a.True(!r.KnownDevices.Single(d => d.Serial == "SERIAL-A").Hidden, "reconnecting restores the tab");
    });

    yield return ("closing the only tab hides it but keeps the phone paired and active (M2a)", a =>
    {
        // M1 guarded close to Count>1, so closing a single-device strip silently did nothing.
        // M2a lets it through: the tab hides, but close ≠ unpair, so the phone stays paired and
        // active, and Settings › Devices can bring it back.
        var r = new DeviceRegistry(root);
        r.SavePairedDevice("SERIAL-A", "Pixel 7");
        r.SavePhoneCert("CERT-A");

        r.SetDeviceHidden("SERIAL-A", hidden: true); // what CloseTab does for the last tab
        a.True(r.KnownDevices.Count == 1, "the phone is still known");
        a.True(r.KnownDevices.Single().Hidden, "its tab is hidden");
        a.True(r.PairedSerial == "SERIAL-A", "it is still the active/paired phone");
        a.True(r.PhoneCertBase64 == "CERT-A", "its certificate survived the close");

        // Settings › Devices "Set active" un-hides even the already-active phone (SetActiveDevice
        // alone would no-op since it's active), which is how the strip gets its last tab back.
        r.SetDeviceHidden("SERIAL-A", hidden: false);
        a.True(!r.KnownDevices.Single().Hidden, "Set active re-opened the tab");
    });

    yield return ("forgetting the active device promotes another one", a =>
    {
        var r = new DeviceRegistry(root);
        r.SavePairedDevice("SERIAL-A", "Pixel 7");
        r.SavePairedDevice("SERIAL-B", "Pixel 9");
        a.True(r.PairedSerial == "SERIAL-B", "B is active");

        r.Clear(); // the Settings page's "Forget device"
        a.True(r.KnownDevices.Count == 1, "B is gone");
        a.True(r.PairedSerial == "SERIAL-A", "A was promoted");

        r.Clear();
        a.True(r.KnownDevices.Count == 0, "nothing is known any more");
        a.True(r.PairedSerial is null, "nothing is active any more");
    });

    yield return ("a pre-M03 settings file migrates into the active device's record", a =>
    {
        // Exactly the shape the app wrote before M03: per-device values as global fields, and
        // no KnownDevices list at all.
        var legacy = """
        {
          "LastHostPort": "192.168.2.9:37000",
          "PairedSerial": "<your-device-serial>",
          "PairedModel": "Pixel 7",
          "ClipboardSyncEnabled": true,
          "NotificationSyncEnabled": true,
          "AllowUsbConnections": true,
          "PhoneCertBase64": "LEGACY-CERT",
          "BackgroundConnectionEnabled": true,
          "SyncLanes": { "folders": true, "photos": true },
          "FolderSyncPcPath": "C:\\Users\\me\\Sync",
          "FolderSyncPhonePath": "/sdcard/Download"
        }
        """;
        var path = Path.Combine(root, "settings.json");
        Directory.CreateDirectory(root);
        File.WriteAllText(path, legacy);

        var r = new DeviceRegistry(root);
        a.True(r.KnownDevices.Count == 1, "the paired phone was adopted into the tab list");
        a.True(r.PairedSerial == "<your-device-serial>", "it is the active device");
        a.True(r.LastHostPort == "192.168.2.9:37000", "the address migrated");
        a.True(r.PhoneCertBase64 == "LEGACY-CERT", "the pinned certificate migrated");
        a.True(r.SyncLane("folders") && r.SyncLane("photos"), "the sync lanes migrated");
        a.True(r.FolderSyncPcPath == @"C:\Users\me\Sync", "the folder pair migrated");

        // Losing the certificate would silently break Direct TLS; losing the lanes would
        // silently stop sync. Both must survive being written back out and re-read.
        r.SaveClipboardSyncEnabled(true); // force a Persist()
        var rewritten = new DeviceRegistry(root);
        a.True(rewritten.PhoneCertBase64 == "LEGACY-CERT", "the certificate survived the rewrite");
        a.True(rewritten.SyncLane("photos"), "the lanes survived the rewrite");
        a.True(rewritten.LastHostPort == "192.168.2.9:37000", "the address survived the rewrite");

        // ...and the legacy globals are gone from the file, not duplicated.
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        a.True(doc.RootElement.GetProperty("PhoneCertBase64").ValueKind == JsonValueKind.Null,
            "the legacy global certificate field is cleared");
    });

    yield return ("the default root still resolves to %LOCALAPPDATA%\\Linc (D-057)", a =>
    {
        // Asserted as a STRING, never by constructing a default registry and writing through it —
        // the whole point of D-057 is that no harness ever opens that file. The production call
        // sites pass no root, so this is what they get.
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Linc");
        a.True(DeviceRegistry.DefaultRootPath == expected,
            $"DefaultRootPath is {expected}");
        a.True(!root.StartsWith(DeviceRegistry.DefaultRootPath, StringComparison.OrdinalIgnoreCase),
            "and this run's temp root is nowhere underneath it");
    });
}

/// <summary>Records pass/fail per claim so one broken scenario doesn't hide the rest.</summary>
internal sealed class Asserter(string scenario, List<string> failures)
{
    public void True(bool condition, string claim)
    {
        Console.WriteLine($"    {(condition ? "ok  " : "FAIL")} {claim}");
        if (!condition)
        {
            failures.Add($"{scenario}: {claim}");
        }
    }
}
