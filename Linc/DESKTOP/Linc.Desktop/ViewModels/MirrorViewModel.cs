using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linc.Desktop.Services;
using Microsoft.UI.Dispatching;

namespace Linc.Desktop.ViewModels;

public partial class MirrorViewModel : ObservableObject
{
    private readonly IMirrorService _mirror;
    private readonly IConnectionSupervisor _supervisor;
    private readonly IDeviceRegistry _registry;
    private readonly DispatcherQueue _dispatcher;

    public IReadOnlyList<MirrorPreset> Presets => _mirror.Presets;

    public MirrorViewModel(
        IMirrorService mirror,
        IConnectionSupervisor supervisor,
        IDeviceRegistry registry)
    {
        _mirror = mirror;
        _supervisor = supervisor;
        _registry = registry;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        SyncSelectedPresetFromSettings();

        _mirror.StateChanged += () => _dispatcher.TryEnqueue(() =>
        {
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(ButtonText));
            OnPropertyChanged(nameof(StatusText));
        });
        _mirror.ErrorRaised += message => _dispatcher.TryEnqueue(() => ErrorMessage = message);

        _supervisor.StateChanged += () => _dispatcher.TryEnqueue(() =>
        {
            if (_supervisor.State != LinkState.Connected && IsRunning)
            {
                _ = _mirror.StopAsync();
            }
            OnPropertyChanged(nameof(CanMirror));
        });

        // When the active device changes (another tab) or its Mirror settings are saved,
        // re-sync the preset combo so it shows the right selection — or none when the
        // persisted values match no preset (no "Custom" entry is invented).
        _registry.ActiveDeviceChanged += () => _dispatcher.TryEnqueue(SyncSelectedPresetFromSettings);
        _registry.MirrorChanged += () => _dispatcher.TryEnqueue(SyncSelectedPresetFromSettings);
    }

    private MirrorPreset? _selectedPreset;
    public MirrorPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (SetProperty(ref _selectedPreset, value) && value is { } preset)
            {
                ApplyPresetToSettings(preset);
            }
        }
    }

    /// <summary>Writing a preset drops its MaxSize + BitRate into the device's persisted
    /// MirrorSettings (MaxFps/Crop/flags are untouched). Persists via the registry so the
    /// next mirror start and any other observer see the change.</summary>
    private void ApplyPresetToSettings(MirrorPreset preset)
    {
        var current = _registry.Mirror;
        _registry.SaveMirror(current with
        {
            MaxSize = preset.MaxSize,
            VideoBitRate = preset.BitRate,
        });
        // SaveMirror fires MirrorChanged, which re-syncs the combo; the selection persists.
    }

    /// <summary>Selects the preset that matches the persisted MaxSize+BitRate exactly,
    /// or clears the selection when none matches (no "Custom" preset is invented — the
    /// task says so explicitly).</summary>
    private void SyncSelectedPresetFromSettings()
    {
        var s = _registry.Mirror;
        var match = _mirror.Presets.FirstOrDefault(p => p.MaxSize == s.MaxSize && p.BitRate == s.VideoBitRate);
        if (match != _selectedPreset)
        {
            _selectedPreset = match;
            OnPropertyChanged(nameof(SelectedPreset));
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool IsRunning => _mirror.State == MirrorState.Running;
    public bool CanMirror => _supervisor.State == LinkState.Connected;
    public bool HasError => ErrorMessage is not null;
    public string ButtonText => IsRunning ? "Stop mirroring" : "Mirror screen";
    public string StatusText => IsRunning
        ? "Your phone's screen is in its own Linc window — mouse and keyboard work inside it."
        : "";

    [RelayCommand]
    private async Task ToggleAsync()
    {
        ErrorMessage = null;
        try
        {
            if (IsRunning)
            {
                await _mirror.StopAsync();
            }
            else if (_supervisor.Device is { } device)
            {
                // Always start from the persisted per-device settings — never from the
                // preset combo directly. The combo is now only a shortcut that writes
                // into MirrorSettings; the source of truth lives in the registry.
                await _mirror.StartAsync(device.Serial, $"Linc — {device.Model}", _registry.Mirror);
            }
        }
        catch (LincException ex)
        {
            ErrorMessage = ex.Message;
        }
    }
}
