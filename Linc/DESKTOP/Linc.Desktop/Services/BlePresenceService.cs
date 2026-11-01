using System.Security.Cryptography;
using Windows.Devices.Bluetooth.Advertisement;

namespace Linc.Desktop.Services;

/// <summary>
/// What the phone's beacon says about it (one status byte after the id, phones from build 13 on;
/// older phones send none). The PC uses it for clues while there is no link ("the phone's Wi-Fi is
/// off", "it's on a network but nobody answers") and to start its hotspot at once when the phone
/// asks for it.
/// </summary>
[Flags]
public enum PhoneBeaconFlags : byte
{
    None = 0,
    Linked = 1,
    WifiOn = 2,
    OnWifi = 4,
    WirelessDebugging = 8,
    OnPcHotspot = 16,
    KnowsPcHotspot = 32,
}

/// <summary>What the PC's own beacon tells the phone (the same byte, the PC's meaning).</summary>
[Flags]
public enum PcBeaconFlags : byte
{
    None = 0,
    Linked = 1,
    HotspotOn = 2,
    Looking = 4,
}

public interface IBlePresenceService : IDisposable
{
    /// <summary>
    /// A known phone's beacon was just heard, identified by serial. Fires repeatedly while the
    /// phone is in range (roughly once a second), so subscribers must be idempotent. May fire on
    /// background threads.
    /// </summary>
    event Action<string>? DeviceSighted;

    /// <summary>The phone's status changed (or was heard for the first time this run). Background thread.</summary>
    event Action<string, PhoneBeaconFlags>? StatusHeard { add { } remove { } }

    /// <summary>True once the watcher is actually scanning — false on a PC with no Bluetooth.</summary>
    bool IsScanning { get; }

    /// <summary>The last status heard from the phone, and when; null before any.</summary>
    (PhoneBeaconFlags Flags, DateTimeOffset At)? LastStatus => null;

    /// <summary>True while this PC's own beacon is on the air.</summary>
    bool IsAdvertising => false;

    void Start();
    void Stop();

    /// <summary>What this PC's beacon says (link, hotspot, looking). Cheap to call often: only a change re-advertises.</summary>
    void SetPcStatus(PcBeaconFlags flags) { }
}

/// <summary>
/// Bluetooth-LE presence (M04, D-034), both ways since build 13 (owner 2026-11-01: "use Bluetooth
/// as a helper alongside the main guessing"). The phone advertises a tiny beacon and this listens
/// for it; this PC advertises one back that the phone listens for. A sighting means "that device
/// is physically near", plus one status byte each way — never content, never a transport.
///
/// Each beacon carries a rotating id derived from the pinned pairing certificate, so only the
/// paired pair can recognise it, and a passive observer can't follow either between rotations.
/// See PROTOCOL.md "BLE presence beacon" for the exact format.
///
/// Everything degrades to a no-op: no radio, Bluetooth off, no permission on the phone, or an
/// unpaired device all simply mean no sightings. Bluetooth turned on later is noticed within 15 s.
/// </summary>
public sealed class BlePresenceService(IDeviceRegistry registry, ILogService log) : IBlePresenceService
{
    /// <summary>
    /// 0xFFFF is the Bluetooth SIG's company id reserved for testing and development — the
    /// correct choice for a beacon from an unregistered project.
    /// </summary>
    private const ushort ManufacturerId = 0xFFFF;

    /// <summary>Marks a phone's Linc beacon inside the 0xFFFF space, which anyone may use.</summary>
    private static readonly byte[] Magic = [0x4C, 0x43]; // "LC"

    /// <summary>Marks this PC's beacon for its phone.</summary>
    private static readonly byte[] PcMagic = [0x4C, 0x50]; // "LP"

    /// <summary>How long one rotating id is valid. Must match the phone's advertiser.</summary>
    private static readonly TimeSpan RotationPeriod = TimeSpan.FromMinutes(5);

    private const int IdLength = 8;

    private readonly object _lock = new();
    private BluetoothLEAdvertisementWatcher? _watcher;
    private BluetoothLEAdvertisementPublisher? _publisher;
    private (PcBeaconFlags Flags, long Slot, string? Serial)? _published;
    private PcBeaconFlags _pcFlags;
    private Timer? _retry;
    private string? _lastUnavailable;
    private DateTimeOffset _lastLoggedSighting = DateTimeOffset.MinValue;
    private readonly Dictionary<string, PhoneBeaconFlags> _lastFlags = [];

    public event Action<string>? DeviceSighted;
    public event Action<string, PhoneBeaconFlags>? StatusHeard;
    public bool IsScanning => _watcher?.Status == BluetoothLEAdvertisementWatcherStatus.Started;
    public bool IsAdvertising => _publisher?.Status == BluetoothLEAdvertisementPublisherStatus.Started;
    public (PhoneBeaconFlags Flags, DateTimeOffset At)? LastStatus { get; private set; }

    public void Start()
    {
        lock (_lock)
        {
            // Bluetooth switched on after Linc started (or the watcher stopped when it went off):
            // look again every 15 s instead of never (owner: "Bluetooth on, PC on, but no try").
            _retry ??= new Timer(_ => Tick(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        }
        StartWatcher();
        Republish();
    }

    private void Tick()
    {
        if (!IsScanning)
        {
            lock (_lock)
            {
                if (_watcher is { } dead)
                {
                    dead.Received -= OnReceived;
                    dead.Stopped -= OnStopped;
                    _watcher = null;
                }
            }
            StartWatcher();
        }
        Republish();
    }

    private void StartWatcher()
    {
        lock (_lock)
        {
            if (_watcher is not null)
            {
                return;
            }
            try
            {
                var watcher = new BluetoothLEAdvertisementWatcher
                {
                    // Passive is enough: everything we need is in the advertisement itself, and it
                    // avoids waking the phone's radio for a scan response it has nothing to add to.
                    ScanningMode = BluetoothLEScanningMode.Passive,
                };
                watcher.AdvertisementFilter.Advertisement.ManufacturerData.Add(
                    new BluetoothLEManufacturerData { CompanyId = ManufacturerId });
                watcher.Received += OnReceived;
                watcher.Stopped += OnStopped;
                watcher.Start();
                _watcher = watcher;
                _lastUnavailable = null;
                log.Log(LogLevel.Info, "Watching for your phone over Bluetooth.");
            }
            catch (Exception ex)
            {
                // No radio, radio off, or the stack refused us. Presence over Wi-Fi is unaffected.
                // WinRT often throws with an empty message here; the HRESULT is the useful part.
                var why = string.IsNullOrWhiteSpace(ex.Message)
                    ? $"{ex.GetType().Name} 0x{ex.HResult:X8}"
                    : ex.Message.Trim();
                if (why != _lastUnavailable)
                {
                    _lastUnavailable = why; // once per reason, not every 15 s
                    log.Log(LogLevel.Info, $"Bluetooth presence is unavailable on this PC for now ({why}); Linc tries again when Bluetooth is on.");
                }
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _retry?.Dispose();
            _retry = null;
            if (_watcher is { } watcher)
            {
                watcher.Received -= OnReceived;
                watcher.Stopped -= OnStopped;
                try
                {
                    watcher.Stop();
                }
                catch (Exception)
                {
                    // Already stopped, or the radio went away underneath us.
                }
                _watcher = null;
            }
            StopPublisher();
        }
    }

    public void Dispose() => Stop();

    // ---- this PC's beacon ----

    public void SetPcStatus(PcBeaconFlags flags)
    {
        if (_pcFlags == flags)
        {
            return;
        }
        _pcFlags = flags;
        Republish();
    }

    /// <summary>
    /// Advertise "LP" ‖ rotating id of the paired phone ‖ status, while that is worth saying: not
    /// linked, or the hotspot is on. Linked with nothing to offer, the radio stays quiet.
    /// </summary>
    private void Republish()
    {
        lock (_lock)
        {
            var flags = _pcFlags;
            var serial = registry.PairedSerial;
            var cert = CertOf(serial);
            bool worth = cert is not null && (!flags.HasFlag(PcBeaconFlags.Linked) || flags.HasFlag(PcBeaconFlags.HotspotOn));
            if (!worth)
            {
                StopPublisher();
                return;
            }
            var slot = CurrentSlot();
            if (_published == (flags, slot, serial) && IsAdvertising)
            {
                return;
            }
            StopPublisher();
            try
            {
                var payload = new byte[PcMagic.Length + IdLength + 1];
                PcMagic.CopyTo(payload, 0);
                RotatingId(cert!, slot).CopyTo(payload, PcMagic.Length);
                payload[^1] = (byte)flags;
                var publisher = new BluetoothLEAdvertisementPublisher();
                publisher.Advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData
                {
                    CompanyId = ManufacturerId,
                    Data = Windows.Security.Cryptography.CryptographicBuffer.CreateFromByteArray(payload),
                });
                publisher.Start();
                _publisher = publisher;
                _published = (flags, slot, serial);
            }
            catch (Exception)
            {
                // Radio off or no LE peripheral role: the phone simply doesn't hear the PC.
                _publisher = null;
                _published = null;
            }
        }
    }

    private void StopPublisher()
    {
        if (_publisher is { } publisher)
        {
            try
            {
                publisher.Stop();
            }
            catch (Exception)
            {
            }
        }
        _publisher = null;
        _published = null;
    }

    private byte[]? CertOf(string? serial)
    {
        if (serial is null || registry.KnownDevices.FirstOrDefault(d => d.Serial == serial)?.PhoneCertBase64 is not { } base64)
        {
            return null;
        }
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // ---- the phone's beacon ----

    private void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        // Usually the radio being switched off. Log it rather than dying silently; Tick starts
        // watching again once Bluetooth is back.
        log.Log(LogLevel.Info, $"Stopped watching for your phone over Bluetooth ({args.Error}); Linc looks again when Bluetooth is back.");
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        foreach (var section in args.Advertisement.ManufacturerData)
        {
            if (section.CompanyId != ManufacturerId)
            {
                continue;
            }
            var payload = new byte[section.Data.Length];
            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(section.Data))
            {
                reader.ReadBytes(payload);
            }
            if (Match(payload) is not { } serial)
            {
                continue;
            }
            DeviceSighted?.Invoke(serial);

            if (payload.Length > Magic.Length + IdLength)
            {
                var flags = (PhoneBeaconFlags)payload[Magic.Length + IdLength];
                LastStatus = (flags, DateTimeOffset.UtcNow);
                bool changed;
                lock (_lastFlags)
                {
                    changed = !_lastFlags.TryGetValue(serial, out var before) || before != flags;
                    _lastFlags[serial] = flags;
                }
                if (changed)
                {
                    StatusHeard?.Invoke(serial, flags);
                }
            }

            // The beacon arrives about once a second; log at most once a minute per run so the
            // activity log stays readable.
            if (DateTimeOffset.UtcNow - _lastLoggedSighting > TimeSpan.FromMinutes(1))
            {
                _lastLoggedSighting = DateTimeOffset.UtcNow;
                var model = registry.KnownDevices.FirstOrDefault(d => d.Serial == serial)?.Model ?? "Your phone";
                log.Log(LogLevel.Info, $"{model} is nearby (seen over Bluetooth).");
            }
        }
    }

    /// <summary>A plain sentence for the phone's status, for the log and Settings.</summary>
    public static string Describe(PhoneBeaconFlags f)
    {
        var parts = new List<string>
        {
            f.HasFlag(PhoneBeaconFlags.Linked) ? "Linc on the phone has a link" : "Linc on the phone has no link",
            !f.HasFlag(PhoneBeaconFlags.WifiOn) ? "its Wi-Fi is off"
                : f.HasFlag(PhoneBeaconFlags.OnPcHotspot) ? "it's on a PC hotspot"
                : f.HasFlag(PhoneBeaconFlags.OnWifi) ? "it's on a Wi-Fi network" : "its Wi-Fi is on but not joined to any network",
            f.HasFlag(PhoneBeaconFlags.WirelessDebugging) ? "wireless debugging is on" : "wireless debugging is off",
        };
        if (f.HasFlag(PhoneBeaconFlags.KnowsPcHotspot)) parts.Add("it knows this PC's hotspot");
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Which known phone this beacon belongs to, or null if it isn't one of ours. Only a device
    /// whose certificate this PC has pinned can match, so an unpaired phone's beacon — or a
    /// stranger reusing the 0xFFFF company id — is simply ignored.
    /// </summary>
    private string? Match(byte[] payload)
    {
        if (payload.Length < Magic.Length + IdLength ||
            payload[0] != Magic[0] || payload[1] != Magic[1])
        {
            return null;
        }
        var advertised = payload.AsSpan(Magic.Length, IdLength);
        var slot = CurrentSlot();
        foreach (var device in registry.KnownDevices)
        {
            if (device.PhoneCertBase64 is not { } certBase64)
            {
                continue; // never paired far enough to share a secret
            }
            byte[] cert;
            try
            {
                cert = Convert.FromBase64String(certBase64);
            }
            catch (FormatException)
            {
                // Expected and routine: a corrupted stored cert shouldn't crash the whole match
                // loop; skip just this device and keep checking the others.
                continue;
            }
            // Accept the neighbouring slots too: the two clocks are independent and a beacon
            // sent just before a rollover would otherwise be unrecognisable for a few seconds.
            for (var offset = -1; offset <= 1; offset++)
            {
                if (advertised.SequenceEqual(RotatingId(cert, slot + offset)))
                {
                    return device.Serial;
                }
            }
        }
        return null;
    }

    private static long CurrentSlot() =>
        DateTimeOffset.UtcNow.ToUnixTimeSeconds() / (long)RotationPeriod.TotalSeconds;

    /// <summary>
    /// The beacon id for a given pairing and time slot: the first 8 bytes of
    /// SHA-256(certificate DER ‖ slot as 8 big-endian bytes). The phone computes the same thing
    /// — see BlePresenceAdvertiser.kt, which must stay in step with this (D-006: the spec is
    /// shared, the code is not). Both beacons use the phone's certificate: it is the one secret
    /// the pair already shares.
    /// </summary>
    private static byte[] RotatingId(byte[] certDer, long slot)
    {
        Span<byte> slotBytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(slotBytes, slot);

        var material = new byte[certDer.Length + 8];
        certDer.CopyTo(material, 0);
        slotBytes.CopyTo(material.AsSpan(certDer.Length));

        return SHA256.HashData(material)[..IdLength];
    }
}
