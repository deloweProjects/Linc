using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Linc.Desktop.Services;

public interface IBatteryAlertService
{
    /// <summary>Idempotent; hooks the supervisor's status stream.</summary>
    void Start();
}

/// <summary>
/// Windows toast when the phone's battery runs low (docs/ROADMAP.md M14). One alert
/// per "low episode": re-arms when the phone charges or climbs back above the
/// re-arm level, so a phone hovering at the threshold doesn't spam.
/// </summary>
public sealed class BatteryAlertService(IConnectionSupervisor supervisor, ILogService log) : IBatteryAlertService
{
    private const int LowThreshold = 15;
    private const int RearmThreshold = 20;

    private bool _started;
    private bool _alerted;

    public void Start()
    {
        if (_started)
        {
            return;
        }
        _started = true;
        supervisor.StatusUpdated += OnStatusUpdated;
    }

    private void OnStatusUpdated(DeviceStatus status)
    {
        if (status.Charging || status.Battery > RearmThreshold)
        {
            _alerted = false;
            return;
        }
        if (status.Battery > LowThreshold || _alerted)
        {
            return;
        }
        _alerted = true;
        try
        {
            var model = supervisor.Device?.Model ?? "Your phone";
            AppNotificationManager.Default.Show(new AppNotificationBuilder()
                .AddText($"{model} is at {status.Battery}%")
                .AddText("Time to find a charger.")
                .BuildNotification());
            log.Log(LogLevel.Info, $"Low-battery alert shown ({status.Battery}%)");
        }
        catch (Exception)
        {
            // Toast infrastructure unavailable; not worth surfacing.
        }
    }
}
