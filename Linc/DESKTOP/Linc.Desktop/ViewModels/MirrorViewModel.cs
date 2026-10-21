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
            // "Fill this monitor" resizes the phone's display for a session; one cut short (Linc
            // closed or crashed, cable pulled) is put back as soon as the phone is here again.
            if (_supervisor.State == LinkState.Connected && _supervisor.Device is { } connected)
            {
                _ = _mirror.RestoreLeftoverAsync(connected.Serial);
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
    /// task says so explicitly). Also refreshes the screen-shape and phone-sound choices.</summary>
    private void SyncSelectedPresetFromSettings()
    {
        var s = _registry.Mirror;
        var match = _mirror.Presets.FirstOrDefault(p => p.MaxSize == s.MaxSize && p.BitRate == s.VideoBitRate);
        if (match != _selectedPreset)
        {
            _selectedPreset = match;
            OnPropertyChanged(nameof(SelectedPreset));
        }
        var shape = ShapeToIndex(s.ScreenShape);
        if (shape != _screenShapeIndex)
        {
            _screenShapeIndex = shape;
            OnPropertyChanged(nameof(ScreenShapeIndex));
        }
        OnPropertyChanged(nameof(FillsMonitor));
        var sound = SoundToIndex(s);
        if (sound != _soundIndex)
        {
            _soundIndex = sound;
            OnPropertyChanged(nameof(SoundIndex));
        }
        OnPropertyChanged(nameof(SoundOnPc));
    }

    // ---- 1.0.0.18: screen shape and phone sound, on the card itself (saved as they change) ----

    private static int ShapeToIndex(string? shape) => shape switch
    {
        MirrorSettings.ShapeMonitor => 1,
        MirrorSettings.ShapeMonitorLandscape => 2,
        _ => 0,
    };

    private int _screenShapeIndex;

    /// <summary>0 = the phone's own shape, 1 = fill this monitor, 2 = fill this monitor turned
    /// sideways. Bound to the ComboBox's SelectedIndex on the Device page.</summary>
    public int ScreenShapeIndex
    {
        get => _screenShapeIndex;
        set
        {
            if (value < 0 || value > 2 || !SetProperty(ref _screenShapeIndex, value))
            {
                return;
            }
            _registry.SaveMirror(_registry.Mirror with
            {
                ScreenShape = value switch
                {
                    1 => MirrorSettings.ShapeMonitor,
                    2 => MirrorSettings.ShapeMonitorLandscape,
                    _ => null,
                },
            });
        }
    }

    public bool FillsMonitor => _registry.Mirror.FillsMonitor();

    // The owner: sound comes out of one device while mirroring, chosen here; both at once is
    // what Play everywhere is for. 0 = on this PC (the phone silent: no --audio-dup),
    // 1 = on the phone (the PC silent: --no-audio). An older "both" setting (sound here with
    // MutePhone off) shows as 0 and becomes "this PC only" when picked.
    private static int SoundToIndex(MirrorSettings s) => s.AudioEnabled ? 0 : 1;

    private int _soundIndex;

    /// <summary>Bound to the Sound ComboBox's SelectedIndex on the Device page.</summary>
    public int SoundIndex
    {
        get => _soundIndex;
        set
        {
            if (value < 0 || value > 1 || !SetProperty(ref _soundIndex, value))
            {
                return;
            }
            _registry.SaveMirror(_registry.Mirror with { AudioEnabled = value == 0, MutePhone = true });
        }
    }

    /// <summary>The phone's sound plays on this PC (the audio source and bit rate apply).</summary>
    public bool SoundOnPc => _registry.Mirror.AudioEnabled;

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
