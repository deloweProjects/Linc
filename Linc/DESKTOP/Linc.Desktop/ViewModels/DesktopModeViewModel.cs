using AdvancedSharpAdbClient.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linc.Desktop.Services;
using Microsoft.UI.Dispatching;

namespace Linc.Desktop.ViewModels;

public partial class DesktopModeViewModel : ObservableObject
{
    private readonly IDesktopLaunchService _launch;
    private readonly IDesktopModeService _setup;
    private readonly IConnectionSupervisor _supervisor;
    private readonly IDeviceRegistry _registry;
    private readonly ILogService _log;
    private readonly DispatcherQueue _dispatcher;

    public DesktopModeViewModel(
        IDesktopLaunchService launch,
        IDesktopModeService setup,
        IConnectionSupervisor supervisor,
        IDeviceRegistry registry,
        ILogService log)
    {
        _launch = launch;
        _setup = setup;
        _supervisor = supervisor;
        _registry = registry;
        _log = log;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        IsSetupPending = _registry.DesktopMode.RebootPending;
        if (IsSetupPending)
        {
            SetupMessage = "Your phone needs a restart to finish Desktop Mode setup.";
        }

        _launch.StateChanged += () => _dispatcher.TryEnqueue(() =>
        {
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(ButtonText));
            OnPropertyChanged(nameof(StatusText));
        });

        _launch.ErrorRaised += message => _dispatcher.TryEnqueue(() => ErrorMessage = message);

        _supervisor.StateChanged += () => _dispatcher.TryEnqueue(() =>
        {
            if (_supervisor.State != LinkState.Connected && IsRunning)
            {
                _ = _launch.StopAsync();
            }
            OnPropertyChanged(nameof(CanStart));
        });

        _registry.DesktopModeChanged += () => _dispatcher.TryEnqueue(() =>
        {
            IsSetupPending = _registry.DesktopMode.RebootPending;
            if (IsSetupPending)
            {
                SetupMessage = "Your phone needs a restart to finish Desktop Mode setup.";
            }
        });
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool IsSetupPending { get; set; }

    [ObservableProperty]
    public partial string? SetupMessage { get; set; }

    public bool IsRunning => _launch.State == DesktopLaunchState.Running;
    public bool CanStart => _supervisor.State == LinkState.Connected;
    public bool HasError => ErrorMessage is not null;
    public string ButtonText => IsRunning ? "Stop Desktop Mode" : "Desktop Mode";
    public string StatusText => IsRunning
        ? "Android Desktop Mode is running in a dedicated window."
        : "";

    [RelayCommand]
    private async Task ToggleAsync()
    {
        ErrorMessage = null;
        try
        {
            if (IsRunning)
            {
                await _launch.StopAsync();
            }
            else if (_supervisor.Device is { } connectedDevice)
            {
                var adbDevice = new DeviceData
                {
                    Serial = connectedDevice.Serial,
                    State = DeviceState.Online
                };

                var result = await _setup.EnsureSetupAppliedAsync(adbDevice, CancellationToken.None);
                switch (result)
                {
                    case DesktopModeSetupResult.AlreadyDone:
                    case DesktopModeSetupResult.Success:
                        await LaunchInternalAsync(connectedDevice.Serial);
                        break;
                    case DesktopModeSetupResult.RebootRequired:
                        _registry.SaveDesktopMode(_registry.DesktopMode with { RebootPending = true });
                        IsSetupPending = true;
                        SetupMessage = "Your phone needs a restart to finish Desktop Mode setup.";
                        break;
                    case DesktopModeSetupResult.Failed:
                        ErrorMessage = "Failed to apply Desktop Mode settings on your phone.";
                        break;
                }
            }
        }
        catch (LincException ex)
        {
            _log.Log(LogLevel.Error, $"Desktop Mode toggle error: {ex}");
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            _log.Log(LogLevel.Error, $"Desktop Mode toggle unexpected error: {ex}");
            ErrorMessage = "Linc couldn't start Desktop Mode. Check the phone connection and try again.";
        }
    }

    [RelayCommand]
    private async Task RebootAsync()
    {
        if (_supervisor.Device is not { } connectedDevice) return;
        ErrorMessage = null;
        try
        {
            var adbDevice = new DeviceData
            {
                Serial = connectedDevice.Serial,
                State = DeviceState.Online
            };

            await _setup.RebootAndWaitAsync(adbDevice, CancellationToken.None);
            _registry.SaveDesktopMode(_registry.DesktopMode with { RebootPending = false });
            IsSetupPending = false;
            SetupMessage = null;
            await LaunchInternalAsync(connectedDevice.Serial);
        }
        catch (LincException ex)
        {
            _log.Log(LogLevel.Error, $"Desktop Mode reboot error: {ex}");
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            _log.Log(LogLevel.Error, $"Desktop Mode reboot unexpected error: {ex}");
            ErrorMessage = "Linc couldn't start Desktop Mode. Check the phone connection and try again.";
        }
    }

    private async Task LaunchInternalAsync(string serial)
    {
        await _launch.StartAsync(serial, _registry.DesktopMode);
        _registry.SaveDesktopMode(_registry.DesktopMode with { SetupCompleted = true });
    }
}
