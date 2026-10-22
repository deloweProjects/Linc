using System.Text.RegularExpressions;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;

namespace Linc.Desktop.Services;

public interface IUsbWatcherService : IDisposable
{
    /// <summary>An ADB-visible device that isn't a wireless (host:port) connection. May fire on background threads.</summary>
    event Action<DeviceData>? UsbDeviceSeen;

    /// <summary>
    /// A USB device ADB can see but cannot use yet — the trust prompt is pending, or it came up
    /// offline. Carries the device and the plain-language sentence for the user. Fires on every
    /// poll while the state lasts; the supervisor de-duplicates. May fire on background threads.
    /// </summary>
    event Action<DeviceData, string>? UsbDeviceUnusable;

    /// <summary>
    /// A USB serial this poll used to see and now does not (M15c B2). Raised once per
    /// disappearance, after <see cref="UsbWatcherService.MissesToDeclareGone"/> consecutive
    /// absences so a single dropped poll cannot kill a healthy link. Carries the bare serial —
    /// there is no <c>DeviceData</c> left to carry. May fire on background threads.
    /// </summary>
    event Action<string>? UsbDeviceGone;

    void Start();
    void Stop();

    /// <summary>
    /// LincLink: also listen to the adb server's own device list (<c>host:track-devices</c>), so a
    /// cable going in or out is noticed at once rather than at the next 3 s poll. The poll stays
    /// as the backstop. Off: the poll alone, as before.
    /// </summary>
    void EnablePush(bool on);
}

/// <summary>
/// Polls the ADB server's device list for USB-attached phones — a USB device is
/// already visible to `adb devices` with no `adb connect` step needed, so this is
/// a much simpler story than the mDNS-based wireless discovery in
/// <see cref="DiscoveryService"/> (kept as its own small file rather than folded in,
/// since that one is mDNS-specific).
/// </summary>
public sealed class UsbWatcherService : IUsbWatcherService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How many consecutive polls a serial must be missing before <see cref="UsbDeviceGone"/>
    /// fires (M15c B3). Two, not one: a single poll can come back empty because the adb server
    /// hiccuped, and killing a live cable link over one bad read would be a worse bug than the
    /// slow detection this replaces. Two costs ~6 s worst case against the health loop's 6-21 s,
    /// and the measured truth it rides on is that a real pull removes the serial in ~0.3 s
    /// (M15c B1, measured on the Pixel 7: 329 ms, no stale entry and no `offline` row), so the
    /// second miss is almost always the very next poll.
    /// </summary>
    public const int MissesToDeclareGone = 2;

    private readonly AdbClient _adb = new();
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Consecutive misses per USB serial this loop has seen at least once. An entry exists only
    /// between a sighting and the gone-report that retires it, so it cannot grow without bound.
    /// </summary>
    private readonly Dictionary<string, int> _misses = [];

    public event Action<DeviceData>? UsbDeviceSeen;
    public event Action<DeviceData, string>? UsbDeviceUnusable;
    public event Action<string>? UsbDeviceGone;

    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }
        _cts = new CancellationTokenSource();
        _ = PollLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        // A restarted watcher starts from "seen nothing", so a serial that was present before the
        // stop cannot be reported gone by the first poll after it.
        _misses.Clear();
        EnablePush(false);
    }

    private CancellationTokenSource? _pushCts;
    private TaskCompletionSource _pollNow = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void EnablePush(bool on)
    {
        if (!on)
        {
            _pushCts?.Cancel();
            _pushCts = null;
            return;
        }
        if (_pushCts is not null)
        {
            return;
        }
        _pushCts = new CancellationTokenSource();
        _ = PushLoopAsync(_pushCts.Token);
    }

    /// <summary>Follow the adb server's device list; any change polls at once. Reconnects if the server restarts.</summary>
    private async Task PushLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            DeviceMonitor? monitor = null;
            try
            {
                monitor = new DeviceMonitor(new AdbSocket(AdbClient.AdbServerEndPoint));
                monitor.DeviceListChanged += (_, _) => _pollNow.TrySetResult();
                await monitor.StartAsync(ct);
                while (!ct.IsCancellationRequested && monitor.IsRunning)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
            }
            catch (OperationCanceledException)
            {
                // stopped
            }
            catch
            {
                // The adb server isn't up yet (or just restarted): the poll covers it meanwhile.
            }
            finally
            {
                if (monitor is not null)
                {
                    try
                    {
                        await monitor.DisposeAsync();
                    }
                    catch
                    {
                        // already gone with the adb server
                    }
                }
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException)
            {
                return; // push turned off, or the watcher stopped
            }
        }
    }

    public void Dispose() => Stop();

    /// <summary>
    /// The state word `adb devices` prints for this state. The admission rules are keyed on that
    /// word rather than on the client library's enum so they stay linkable into a plain net8.0
    /// harness — this one switch is the whole cost of that.
    /// </summary>
    private static string AdbStateWord(DeviceState state) => state switch
    {
        DeviceState.Online => DeviceAdmission.OnlineState,
        DeviceState.Unauthorized => DeviceAdmission.UnauthorizedState,
        DeviceState.Offline => "offline",
        _ => state.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Count a poll against every serial this loop has seen and <paramref name="usable"/> no
    /// longer lists, and report the ones that have now missed <see cref="MissesToDeclareGone"/>
    /// in a row. Reporting retires the entry, so one departure raises exactly one event; the
    /// serial has to be seen again before it can go missing again.
    /// </summary>
    private void ReportDepartures(HashSet<string> usable)
    {
        List<string>? gone = null;
        foreach (var serial in _misses.Keys.ToList())
        {
            if (usable.Contains(serial))
            {
                continue;
            }
            if (++_misses[serial] < MissesToDeclareGone)
            {
                continue;
            }
            _misses.Remove(serial);
            (gone ??= []).Add(serial);
        }
        foreach (var serial in gone ?? [])
        {
            UsbDeviceGone?.Invoke(serial);
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var devices = await _adb.GetDevicesAsync(ct);
                // M15c B2/B3: which USB serials this pass can actually talk to. `offline` counts
                // as absent here even though adb still lists the row — a link that cannot carry
                // traffic is dead for the supervisor's purposes, which is the "single offline
                // reading plus one confirm" the task allows, folded into the same counter.
                var usable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var device in devices)
                {
                    if (!AdbSerial.IsNetwork(device.Serial) &&
                        device.State == DeviceState.Online &&
                        !string.IsNullOrEmpty(device.Serial))
                    {
                        usable.Add(device.Serial);
                    }
                }
                ReportDepartures(usable);
                foreach (var device in devices)
                {
                    if (AdbSerial.IsNetwork(device.Serial))
                    {
                        continue; // a wireless connection (IPv4, IPv6 or mDNS), not a cable — D-078
                    }
                    if (device.State == DeviceState.Online)
                    {
                        _misses[device.Serial] = 0; // present again; any part-way streak is void
                        UsbDeviceSeen?.Invoke(device);
                        continue;
                    }
                    // M15a A3: a phone whose trust prompt is pending reports `unauthorized`, and
                    // this loop used to drop it on the floor — so plugging in a brand-new phone
                    // looked to the user exactly like plugging in nothing at all. Report it
                    // instead. Recovery needs no extra machinery and no fixed sleep: this poll is
                    // already running, and the moment the user taps Allow the very next pass sees
                    // `device` and takes the UsbDeviceSeen branch above.
                    var trouble = DeviceAdmission.DescribeUnusableState(AdbStateWord(device.State), device.Model);
                    if (trouble is not null)
                    {
                        UsbDeviceUnusable?.Invoke(device, trouble);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Race on shutdown: Stop()/Dispose() cancelled us mid-poll; exit cleanly.
                break;
            }
            catch
            {
                // ADB server hiccup; keep polling.
            }

            try
            {
                // The poll's pace, or at once when the adb server reports a change (EnablePush).
                var now = _pollNow;
                await Task.WhenAny(Task.Delay(PollInterval, ct), now.Task);
                ct.ThrowIfCancellationRequested();
                if (now.Task.IsCompleted)
                {
                    Interlocked.CompareExchange(ref _pollNow, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), now);
                }
            }
            catch (OperationCanceledException)
            {
                // Race on shutdown: Stop()/Dispose() cancelled us during the poll delay; exit cleanly.
                break;
            }
        }
    }
}
