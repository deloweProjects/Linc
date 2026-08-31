using CommunityToolkit.Mvvm.ComponentModel;
using Linc.Desktop.Services;
using Microsoft.UI.Dispatching;

namespace Linc.Desktop.ViewModels;

/// <summary>
/// "Quirky fun details" fed by the connected phone's protocol v3 status snapshot
/// (CPU/RAM/Wi-Fi/uptime, refreshed on the same 15 s cadence as the Device page's
/// battery/storage — no extra polling), plus session counters from other services.
/// </summary>
public partial class DetailsViewModel : ObservableObject
{
    private readonly IMirrorService _mirror;
    private readonly INotificationSyncService _notifications;

    public DetailsViewModel(
        IConnectionSupervisor supervisor,
        IMirrorService mirror,
        INotificationSyncService notifications)
    {
        _mirror = mirror;
        _notifications = notifications;

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        supervisor.StatusUpdated += status => dispatcher.TryEnqueue(() => OnStatusUpdated(status));
        mirror.StateChanged += () => dispatcher.TryEnqueue(RefreshSessionStats);
        notifications.Changed += () => dispatcher.TryEnqueue(RefreshSessionStats);

        CpuText = "—";
        RamText = "—";
        WifiText = "—";
        UptimeText = "—";
        RefreshSessionStats();
    }

    [ObservableProperty]
    public partial string CpuText { get; set; }

    [ObservableProperty]
    public partial string RamText { get; set; }

    [ObservableProperty]
    public partial string WifiText { get; set; }

    [ObservableProperty]
    public partial string UptimeText { get; set; }

    [ObservableProperty]
    public partial string MirrorStatsText { get; set; } = "";

    [ObservableProperty]
    public partial string NotificationStatsText { get; set; } = "";

    private void OnStatusUpdated(DeviceStatus status)
    {
        CpuText = status.CpuLoad1m is { } load ? $"{load:0.00}" : "Not available on this phone";

        RamText = status.RamUsedBytes is { } used && status.RamTotalBytes is { } total and > 0
            ? $"{Gb(used)} GB of {Gb(total)} GB in use"
            : "Not available on this phone";

        WifiText = status.WifiSignalLevel is { } level
            ? $"{SignalWord(level)} · {status.WifiLinkSpeedMbps ?? 0} Mbps"
            : "Not connected over Wi-Fi";

        UptimeText = FormatUptime(status.UptimeMillis);
    }

    private void RefreshSessionStats()
    {
        MirrorStatsText = _mirror.SessionCount == 0
            ? "No mirroring sessions yet this run"
            : $"{_mirror.SessionCount} session(s), {FormatDuration(_mirror.TotalSessionDuration)} total";

        NotificationStatsText =
            $"{_notifications.RelayedCount} relayed · {_notifications.DismissedCount} dismissed this session";
    }

    private static string SignalWord(int level) => level switch
    {
        <= 0 => "Very weak",
        1 => "Weak",
        2 => "Okay",
        3 => "Good",
        _ => "Excellent",
    };

    private static string FormatUptime(long millis)
    {
        var span = TimeSpan.FromMilliseconds(millis);
        return span.TotalDays >= 1
            ? $"Awake for {(int)span.TotalDays}d {span.Hours}h"
            : $"Awake for {(int)span.TotalHours}h {span.Minutes}m";
    }

    private static string FormatDuration(TimeSpan span) => span.TotalHours >= 1
        ? $"{(int)span.TotalHours}h {span.Minutes}m"
        : $"{span.Minutes}m {span.Seconds}s";

    private static string Gb(long bytes) => (bytes / 1_000_000_000.0).ToString("0.0");
}
