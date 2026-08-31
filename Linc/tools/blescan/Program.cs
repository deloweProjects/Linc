using Linc.Desktop.Services;
using Windows.Devices.Bluetooth.Advertisement;

// End-to-end check of BLE presence (M04 / D-034) against the REAL BlePresenceService and the
// REAL DeviceRegistry, so a sighting here proves the phone's advertiser and the desktop's
// matcher agree on the beacon format and the rotating-id derivation — the one thing that can
// silently disagree across two independent implementations (D-006).
//
//   dotnet run --project tools/blescan            (30 s)
//   dotnet run --project tools/blescan -- 90      (custom duration, seconds)
//
// Read-only: it scans, it never advertises and never writes settings.
//
// D-057: this is the one harness whose whole purpose is the owner's REAL pairings — it has to
// read the pinned certificate of a real phone to recognise its beacon, so a temp root would make
// it useless (no known devices, instant fail). It calls no Save* method and never constructs a
// registry that could Persist(), so it only ever reads. The root is passed explicitly all the
// same, so every construction site in tools/ states which store it means.

var seconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 30;

var registry = new DeviceRegistry(DeviceRegistry.DefaultRootPath);
Console.WriteLine($"Linc BLE presence scan — {seconds}s");
Console.WriteLine();

if (registry.KnownDevices.Count == 0)
{
    Console.WriteLine("No paired phones. Pair one first, or there is nothing to recognise.");
    return 1;
}
Console.WriteLine("Known devices:");
foreach (var device in registry.KnownDevices)
{
    var pinned = device.PhoneCertBase64 is not null;
    Console.WriteLine($"  {device.Model} ({device.Serial}) — certificate pinned: {pinned}");
    if (!pinned)
    {
        Console.WriteLine("      ...so its beacon can never be recognised: no shared secret.");
    }
}
Console.WriteLine();

// A raw watcher alongside the real service, so a silent run can be told apart from a run where
// the phone is advertising something we fail to match — very different problems.
var rawBeacons = 0;
var raw = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
raw.AdvertisementFilter.Advertisement.ManufacturerData.Add(
    new BluetoothLEManufacturerData { CompanyId = 0xFFFF });
raw.Received += (_, e) =>
{
    foreach (var section in e.Advertisement.ManufacturerData)
    {
        if (section.CompanyId != 0xFFFF) continue;
        var bytes = new byte[section.Data.Length];
        using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(section.Data)) reader.ReadBytes(bytes);
        if (bytes.Length >= 2 && bytes[0] == 0x4C && bytes[1] == 0x43)
        {
            if (Interlocked.Increment(ref rawBeacons) <= 3)
            {
                Console.WriteLine($"  [raw] Linc-shaped beacon, {bytes.Length} bytes: {Convert.ToHexString(bytes)}");
            }
        }
    }
};

var sightings = new Dictionary<string, int>();
var service = new BlePresenceService(registry, new LogService(registry));
service.DeviceSighted += serial =>
{
    lock (sightings)
    {
        sightings[serial] = sightings.GetValueOrDefault(serial) + 1;
        if (sightings[serial] == 1)
        {
            Console.WriteLine($"  SIGHTED {serial}");
        }
    }
};

service.Start();
try { raw.Start(); } catch (Exception ex) { Console.WriteLine($"  (raw watcher unavailable: {ex.Message})"); }

if (!service.IsScanning)
{
    Console.WriteLine("FAIL  the watcher did not start — no Bluetooth radio, or it is switched off.");
    return 1;
}
Console.WriteLine($"Scanning for {seconds}s...");
await Task.Delay(TimeSpan.FromSeconds(seconds));
service.Stop();
try { raw.Stop(); } catch (Exception) { /* already stopped */ }

Console.WriteLine();
if (sightings.Count > 0)
{
    foreach (var (serial, count) in sightings)
    {
        var model = registry.KnownDevices.FirstOrDefault(d => d.Serial == serial)?.Model ?? "unknown";
        Console.WriteLine($"PASS  {model} ({serial}) sighted {count}x — the beacon matched a pinned pairing.");
    }
    return 0;
}

Console.WriteLine($"FAIL  no known phone was recognised (Linc-shaped beacons seen: {rawBeacons}).");
Console.WriteLine(rawBeacons > 0
    ? "      The phone IS advertising but the id did not match: the two rotating-id\n" +
      "      derivations disagree, or this PC has pinned a different certificate."
    : "      Nothing was advertising: the companion isn't running, Bluetooth is off on the\n" +
      "      phone, or BLUETOOTH_ADVERTISE was never granted.");
return 1;
