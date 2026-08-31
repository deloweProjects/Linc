using System.Security.Cryptography;
using Windows.Devices.Bluetooth.Advertisement;

namespace Linc.Desktop.Services;

public interface IBlePresenceService : IDisposable
{
    /// <summary>
    /// A known phone's beacon was just heard, identified by serial. Fires repeatedly while the
    /// phone is in range (roughly once a second), so subscribers must be idempotent. May fire on
    /// background threads.
    /// </summary>
    event Action<string>? DeviceSighted;

    /// <summary>True once the watcher is actually scanning — false on a PC with no Bluetooth.</summary>
    bool IsScanning { get; }

    void Start();
    void Stop();
}

/// <summary>
/// Bluetooth-LE presence (M04, D-034): the phone advertises a tiny beacon and this listens for
/// it. A sighting means "that phone is physically near", nothing more — it is a hint that drives
/// the Nearby indicator and an immediate Wi-Fi discovery pass. <b>No data ever rides BLE.</b>
///
/// The beacon carries a rotating id derived from the pinned pairing certificate, so only a PC
/// that has paired with the phone can recognise it, and a passive observer can't follow the
/// phone between rotations. See docs/PROTOCOL.md "BLE presence beacon" for the exact format.
///
/// Everything here degrades to a no-op: no Bluetooth radio, no permission on the phone, or an
/// unpaired device all simply mean no sightings, and the rest of Linc behaves as it always has.
/// </summary>
public sealed class BlePresenceService(IDeviceRegistry registry, ILogService log) : IBlePresenceService
{
    /// <summary>
    /// 0xFFFF is the Bluetooth SIG's company id reserved for testing and development — the
    /// correct choice for a beacon from an unregistered project.
    /// </summary>
    private const ushort ManufacturerId = 0xFFFF;

    /// <summary>Marks a Linc beacon inside the 0xFFFF space, which anyone may use.</summary>
    private static readonly byte[] Magic = [0x4C, 0x43]; // "LC"

    /// <summary>How long one rotating id is valid. Must match the phone's advertiser.</summary>
    private static readonly TimeSpan RotationPeriod = TimeSpan.FromMinutes(5);

    private const int IdLength = 8;

    private BluetoothLEAdvertisementWatcher? _watcher;
    private DateTimeOffset _lastLoggedSighting = DateTimeOffset.MinValue;

    public event Action<string>? DeviceSighted;
    public bool IsScanning => _watcher?.Status == BluetoothLEAdvertisementWatcherStatus.Started;

    public void Start()
    {
        if (_watcher is not null)
        {
            return;
        }
        try
        {
            _watcher = new BluetoothLEAdvertisementWatcher
            {
                // Passive is enough: everything we need is in the advertisement itself, and it
                // avoids waking the phone's radio for a scan response it has nothing to add to.
                ScanningMode = BluetoothLEScanningMode.Passive,
            };
            _watcher.AdvertisementFilter.Advertisement.ManufacturerData.Add(
                new Windows.Devices.Bluetooth.Advertisement.BluetoothLEManufacturerData
                {
                    CompanyId = ManufacturerId,
                });
            _watcher.Received += OnReceived;
            _watcher.Stopped += OnStopped;
            _watcher.Start();
            log.Log(LogLevel.Info, "Watching for your phone over Bluetooth.");
        }
        catch (Exception ex)
        {
            // No radio, radio off, or the stack refused us. Presence over Wi-Fi is unaffected.
            _watcher = null;
            log.Log(LogLevel.Info, $"Bluetooth presence is unavailable on this PC: {ex.Message}");
        }
    }

    public void Stop()
    {
        if (_watcher is null)
        {
            return;
        }
        _watcher.Received -= OnReceived;
        _watcher.Stopped -= OnStopped;
        try
        {
            _watcher.Stop();
        }
        catch (Exception)
        {
            // Already stopped, or the radio went away underneath us.
        }
        _watcher = null;
    }

    public void Dispose() => Stop();

    private void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        // Usually the radio being switched off. Log it rather than dying silently — a presence
        // feature that stops working without a word is exactly the bug factory BRAIN warns about.
        log.Log(LogLevel.Info, $"Stopped watching for your phone over Bluetooth ({args.Error}).");
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
    /// shared, the code is not).
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
