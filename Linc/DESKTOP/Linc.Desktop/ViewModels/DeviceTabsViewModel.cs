using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linc.Desktop.Services;
using Microsoft.UI.Dispatching;

namespace Linc.Desktop.ViewModels;

/// <summary>One phone's tab in the title-bar strip (M03, D-033/D-037).</summary>
public partial class DeviceTabViewModel : ObservableObject
{
    // The commands live on the item rather than the strip so the DataTemplate can x:Bind them
    // directly — x:Bind inside a template is scoped to the item, and reaching back out to the
    // parent view model needs binding gymnastics for no benefit.
    public DeviceTabViewModel(string serial, string model, Action<DeviceTabViewModel> select, Action<DeviceTabViewModel> close)
    {
        Serial = serial;
        Model = model;
        SelectCommand = new RelayCommand(() => select(this));
        CloseCommand = new RelayCommand(() => close(this));
    }

    public string Serial { get; }
    public RelayCommand SelectCommand { get; }
    public RelayCommand CloseCommand { get; }

    [ObservableProperty]
    public partial string Model { get; set; }

    /// <summary>"Connected" / "Nearby" / "Last seen 14:32" — the at-a-glance presence (D-033).</summary>
    [ObservableProperty]
    public partial string Presence { get; set; } = "Not seen yet";

    /// <summary>Drives the dot's colour: connected phones are accented, everything else is muted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotLive))]
    public partial bool IsLive { get; set; }

    /// <summary>The tab the pages are currently rendering.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>x:Bind can't negate, so the muted presence dot needs its own property.</summary>
    public bool IsNotLive => !IsLive;
}

/// <summary>
/// The Chrome-style device tab strip. Exactly one device is <i>active</i> at a time — its tab is
/// the live one and every page renders it; the other tabs show what M02 cached for that phone
/// (D-037 explains why this is one switchable bundle rather than N concurrent ones).
/// </summary>
public partial class DeviceTabsViewModel : ObservableObject
{
    private static readonly TimeSpan NearbyWindow = TimeSpan.FromSeconds(45);

    private readonly IDeviceRegistry _registry;
    private readonly IConnectionSupervisor _supervisor;
    private readonly IDeviceCacheService _cache;
    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<string, DateTimeOffset> _sightings = [];
    private readonly DispatcherQueueTimer _refreshTimer;

    public DeviceTabsViewModel(
        IDeviceRegistry registry,
        IConnectionSupervisor supervisor,
        IDiscoveryService discovery,
        IUsbWatcherService usbWatcher,
        IBlePresenceService blePresence,
        IDeviceCacheService cache)
    {
        _registry = registry;
        _supervisor = supervisor;
        _cache = cache;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        Rebuild();

        registry.KnownDevicesChanged += () => _dispatcher.TryEnqueue(Rebuild);
        registry.ActiveDeviceChanged += () => _dispatcher.TryEnqueue(Rebuild);
        supervisor.StateChanged += () => _dispatcher.TryEnqueue(RefreshPresence);

        // A sighting on either transport is what "Nearby" means: the phone is reachable but we
        // aren't (or can't be) connected to it, because only the active tab holds a link.
        discovery.ConnectServiceSeen += service =>
        {
            foreach (var tab in Tabs)
            {
                if (service.InstanceName.Contains(tab.Serial, StringComparison.OrdinalIgnoreCase))
                {
                    RecordSighting(tab.Serial);
                }
            }
        };
        usbWatcher.UsbDeviceSeen += device => RecordSighting(device.Serial);

        // Bluetooth is the only one of the three that works when the phone's Wi-Fi is off —
        // which is exactly the case D-034 exists for: "it's here, just not reachable yet".
        blePresence.DeviceSighted += RecordSighting;

        // Presence text goes stale on its own (a sighting ages out of the Nearby window), so it
        // needs a heartbeat as well as the event-driven refreshes.
        _refreshTimer = _dispatcher.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(10);
        _refreshTimer.Tick += (_, _) => RefreshPresence();
        _refreshTimer.Start();
    }

    public ObservableCollection<DeviceTabViewModel> Tabs { get; } = [];

    /// <summary>Visible as soon as one phone is paired, so a single-device owner sees their tab
    /// and the "+" to add another (M1). Only a device-less desktop hides the strip.</summary>
    public bool IsStripVisible => Tabs.Count >= 1;

    /// <summary>
    /// The mirror image of <see cref="IsStripVisible"/>: true when there are no visible tabs, so a
    /// lone "pair a phone" button can stand in for the hidden strip. This is the M2b fix for the
    /// M2a wrinkle — closing the last tab took the strip's "+" with it, stranding the user with no
    /// way back to pairing (D-033 keeps the phone paired but hidden).
    /// </summary>
    public bool IsStripHidden => Tabs.Count == 0;

    /// <summary>Raised when the user picks the "+" tab, so the shell can open the pairing page.</summary>
    public event Action? AddDeviceRequested;

    [RelayCommand]
    private void AddDevice() => AddDeviceRequested?.Invoke();

    private void SelectDevice(DeviceTabViewModel tab)
    {
        if (tab.Serial == _registry.PairedSerial)
        {
            return;
        }
        // Everything else follows from this one call: the registry repoints its per-device
        // settings, the supervisor drops the old link and hunts the new phone, and the cache
        // reloads that serial's copy so the pages have something to draw immediately.
        _registry.SetActiveDevice(tab.Serial);
    }

    /// <summary>Closing a tab hides it; the pairing, certificate and cache all survive (D-033).</summary>
    private void CloseTab(DeviceTabViewModel tab)
    {
        if (tab.Serial == _registry.PairedSerial)
        {
            // Closing the active tab moves to another visible device first, so the pages are never
            // orphaned. When this is the only tab there is no neighbour: the device stays paired
            // (close ≠ unpair, D-033) and the strip simply empties — Settings › Devices brings it
            // back with "Set active" (which un-hides it). M1 guarded this to Count>1, so closing a
            // single-device strip silently no-op'd; that guard is gone.
            var next = Tabs.FirstOrDefault(t => t.Serial != tab.Serial);
            if (next is not null)
            {
                _registry.SetActiveDevice(next.Serial);
            }
        }
        _registry.SetDeviceHidden(tab.Serial, hidden: true);
    }

    private void RecordSighting(string serial)
    {
        _sightings[serial] = DateTimeOffset.UtcNow;
        _dispatcher.TryEnqueue(RefreshPresence);
    }

    private void Rebuild()
    {
        var visible = _registry.KnownDevices.Where(d => !d.Hidden).ToList();

        // Reuse existing tab instances so selection and bindings don't flicker on every rebuild.
        for (var i = Tabs.Count - 1; i >= 0; i--)
        {
            if (!visible.Any(d => d.Serial == Tabs[i].Serial))
            {
                Tabs.RemoveAt(i);
            }
        }
        for (var i = 0; i < visible.Count; i++)
        {
            var existing = Tabs.FirstOrDefault(t => t.Serial == visible[i].Serial);
            if (existing is null)
            {
                Tabs.Insert(Math.Min(i, Tabs.Count),
                    new DeviceTabViewModel(visible[i].Serial, visible[i].Model, SelectDevice, CloseTab));
            }
            else
            {
                existing.Model = visible[i].Model;
            }
        }
        OnPropertyChanged(nameof(IsStripVisible));
        OnPropertyChanged(nameof(IsStripHidden));
        RefreshPresence();
    }

    private void RefreshPresence()
    {
        var active = _registry.PairedSerial;
        foreach (var tab in Tabs)
        {
            tab.IsActive = tab.Serial == active;
            var connected = tab.IsActive && _supervisor.State == LinkState.Connected;
            tab.IsLive = connected;
            tab.Presence = connected ? "Connected" : DescribePresence(tab.Serial);
        }
        OnPropertyChanged(nameof(IsStripVisible));
        OnPropertyChanged(nameof(IsStripHidden));
    }

    private string DescribePresence(string serial)
    {
        if (_sightings.TryGetValue(serial, out var seen) && DateTimeOffset.UtcNow - seen < NearbyWindow)
        {
            return "Nearby";
        }
        // The cache only holds the active device's copy in memory; for the others read the
        // stamp off disk, which is cheap and only happens on the presence heartbeat.
        var lastSeen = serial == _registry.PairedSerial
            ? _cache.Current?.LastSeenUtc
            : _cache.LastSeen(serial);
        return lastSeen is { } stamp
            ? $"Last seen {stamp.ToLocalTime():HH:mm}"
            : "Not seen yet";
    }
}
