using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linc.Desktop.Services;
using Microsoft.UI.Dispatching;

namespace Linc.Desktop.ViewModels;

/// <summary>
/// Edits one device's <see cref="DesktopModeSettings"/>. Loaded from <see cref="IDeviceRegistry.DesktopMode"/>
/// and saved back via <see cref="IDeviceRegistry.SaveDesktopMode"/>. Settings apply to the next Desktop
/// Mode launch, not the running session — the UI caption (DevicePage.xaml) says so. Validation surfaces
/// plain-language messages via <see cref="ValidationMessage"/>; invalid input does not save.
/// </summary>
public partial class DesktopModeSettingsViewModel : ObservableObject
{
    // Pure validation lives on the dependency-free DesktopModeSettings record
    // (DesktopModeSettings.ValidateSettingsFields) so the desktopsim harness can test it
    // without constructing this VM (which needs DispatcherQueue / WinUI).

    // Persistent field declarations (the private ctor below exists only to suppress
    // the CS8618 "non-nullable field not set" warning that the source generator raises
    // for the partial properties above — the real public ctor always sets these).
#pragma warning disable CS8618
    private DesktopModeSettingsViewModel()
    {
    }
#pragma warning restore CS8618
    private readonly IConnectionSupervisor _supervisor;
    private readonly IDeviceRegistry _registry;
    private readonly IDesktopModeService _desktopModeService;
    private readonly ILogService _log;
    private readonly DispatcherQueue _dispatcher;

    // Snapshot the loaded settings so IsDirty can compare against them. Width/Height/Dpi 0 mean
    // "compute a fallback on launch" (DesktopLaunchService.ResolveGeometry); we store the field
    // values verbatim and only validate the non-fallback numeric ranges when the user types a real
    // number (a 0 is allowed: it means auto).
    private DesktopModeSettings _loaded = new();

    public DesktopModeSettingsViewModel(
        IDesktopModeService desktopModeService,
        IConnectionSupervisor supervisor,
        IDeviceRegistry registry,
        ILogService log)
    {
        _desktopModeService = desktopModeService;
        _supervisor = supervisor;
        _registry = registry;
        _log = log;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        // Keep this VM's view fresh if the active device changes (per-device settings, D-037).
        _registry.ActiveDeviceChanged += () => _dispatcher.TryEnqueue(Load);
        _registry.DesktopModeChanged += () => _dispatcher.TryEnqueue(Load);
        // M11 B2.4: the mirror's own AudioEnabled feeds AudioConflictWarning below, so a change on
        // THAT side (not just this one) must also re-raise it — the M6a composed-property trap.
        _registry.MirrorChanged += () => _dispatcher.TryEnqueue(RaiseAudioConflictWarning);

        Load();
    }

    // Exposed so the ComboBoxes can bind SelectedItem. The enum members are bound directly.
    public IReadOnlyList<AspectRatioMode> AspectModes { get; } =
        [AspectRatioMode.MatchMonitor, AspectRatioMode.MatchPhone, AspectRatioMode.Custom];

    [ObservableProperty]
    public partial int Width { get; set; }

    [ObservableProperty]
    public partial int Height { get; set; }

    [ObservableProperty]
    public partial int Dpi { get; set; }

    private AspectRatioMode _aspectMode;
    public AspectRatioMode AspectMode
    {
        get => _aspectMode;
        set
        {
            if (SetProperty(ref _aspectMode, value))
            {
                RecomputeDirty();
            }
        }
    }

    [ObservableProperty]
    public partial bool CaptureMouse { get; set; }

    [ObservableProperty]
    public partial int MaxFps { get; set; }

    private string _videoBitRate = "8M";
    public string VideoBitRate
    {
        get => _videoBitRate;
        set
        {
            if (SetProperty(ref _videoBitRate, value ?? string.Empty))
            {
                RecomputeDirty();
            }
        }
    }

    [ObservableProperty]
    public partial bool ForwardAudio { get; set; }

    /// <summary>M11 B2.4: scrcpy does not arbitrate two simultaneous audio consumers on the same
    /// phone, so if screen mirroring already has its own audio on, turning this on too means only
    /// one of the two may actually get sound. A plain-language warning, not a block — B2.4
    /// explicitly forbids building real stream arbitration.</summary>
    public string? AudioConflictWarning => ForwardAudio && _registry.Mirror.AudioEnabled
        ? "Screen mirroring already has audio on. Audio usually only reaches one of the two at a time, so whichever you start second may end up silent."
        : null;
    public bool HasAudioConflictWarning => AudioConflictWarning is not null;

    private void RaiseAudioConflictWarning()
    {
        OnPropertyChanged(nameof(AudioConflictWarning));
        OnPropertyChanged(nameof(HasAudioConflictWarning));
    }

    private bool _isDirty;
    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    private string? _validationMessage;
    public string? ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetProperty(ref _validationMessage, value))
            {
                OnPropertyChanged(nameof(HasValidationMessage));
            }
        }
    }
    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

    private string? _statusMessage;
    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatusMessage));
            }
        }
    }
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    /// <summary>Loads the active device's settings into the editable fields, clearing any
    /// pending dirty/validation state. Called once from the ctor and on ActiveDeviceChanged /
    /// DesktopModeChanged so switching tabs always shows the right phone's settings.</summary>
    public void Load()
    {
        var s = _registry.DesktopMode;
        _loaded = s;
        _syncing = true;
        try
        {
            Width = s.VirtualDisplayWidth;
            Height = s.VirtualDisplayHeight;
            Dpi = s.VirtualDisplayDpi;
            AspectMode = s.AspectRatioMode;
            CaptureMouse = s.CaptureMouse;
            MaxFps = s.MaxFps;
            VideoBitRate = s.VideoBitRate;
            ForwardAudio = s.ForwardAudio;
        }
        finally
        {
            _syncing = false;
        }
        ValidationMessage = null;
        StatusMessage = null;
        RecomputeDirty();
    }

    // Suppress the per-property dirty recompute while Load() pushes values back.
    private bool _syncing;

    partial void OnWidthChanged(int value) => RecomputeDirty();
    partial void OnHeightChanged(int value) => RecomputeDirty();
    partial void OnDpiChanged(int value) => RecomputeDirty();
    partial void OnCaptureMouseChanged(bool value) => RecomputeDirty();
    partial void OnMaxFpsChanged(int value) => RecomputeDirty();
    partial void OnForwardAudioChanged(bool value)
    {
        RecomputeDirty();
        RaiseAudioConflictWarning();
    }

    private void RecomputeDirty()
    {
        if (_syncing)
        {
            return;
        }
        var dirty = Width != _loaded.VirtualDisplayWidth
            || Height != _loaded.VirtualDisplayHeight
            || Dpi != _loaded.VirtualDisplayDpi
            || AspectMode != _loaded.AspectRatioMode
            || CaptureMouse != _loaded.CaptureMouse
            || MaxFps != _loaded.MaxFps
            || !string.Equals(VideoBitRate, _loaded.VideoBitRate, StringComparison.Ordinal)
            || ForwardAudio != _loaded.ForwardAudio;
        IsDirty = dirty;
        // Clear a stale validation message once the user has changed something — the next
        // Save re-runs validation. Don't clear a current failure if nothing changed.
        if (dirty && !string.IsNullOrWhiteSpace(ValidationMessage))
        {
            ValidationMessage = null;
        }
    }

    /// <summary>Validates the editable fields. Returns null when everything is fine, otherwise a
    /// plain-language message. Allows 0 for width/height/dpi (the "compute a fallback" value).
    /// Delegates to <see cref="DesktopModeSettings.ValidateSettingsFields"/> so the desktopsim
    /// harness can exercise the same rules without the VM.</summary>
    internal static string? ValidateFields(int width, int height, int dpi, int maxFps, string bitRate)
        => DesktopModeSettings.ValidateSettingsFields(width, height, dpi, maxFps, bitRate);

    /// <summary>Public Validate used by the Save command; delegates to the pure helper.</summary>
    public string? Validate() => ValidateFields(Width, Height, Dpi, MaxFps, VideoBitRate);

    [RelayCommand]
    private void Save()
    {
        var error = Validate();
        if (error is not null)
        {
            ValidationMessage = error;
            StatusMessage = null;
            return;
        }
        ValidationMessage = null;
            // The windowing-related fields (DefaultWindowMode / ResizableWindows /
            // AutoFullscreenApps / LaunchAppsFreeform) are no-op on devices that do not
            // declare the freeform-windows feature (D-053) and were removed from this panel;
            // they carry over untouched from the loaded settings so persisted JSON is stable.
            var settings = new DesktopModeSettings(
                VirtualDisplayWidth: Width,
                VirtualDisplayHeight: Height,
                VirtualDisplayDpi: Dpi,
                AspectRatioMode: AspectMode,
                DefaultWindowMode: _loaded.DefaultWindowMode,
                ResizableWindows: _loaded.ResizableWindows,
                AutoFullscreenApps: _loaded.AutoFullscreenApps,
                SetupCompleted: _loaded.SetupCompleted,
                RebootPending: _loaded.RebootPending,
                CaptureMouse: CaptureMouse,
                MaxFps: MaxFps,
                VideoBitRate: VideoBitRate,
                ForwardAudio: ForwardAudio,
                LaunchAppsFreeform: _loaded.LaunchAppsFreeform);
        _registry.SaveDesktopMode(settings);
        _loaded = settings;
        RecomputeDirty();
        StatusMessage = "Settings saved. They apply the next time Desktop Mode starts.";
    }

    [RelayCommand]
    private async Task ResetToRecommendedAsync()
    {
        if (_supervisor.Device is not { } connectedDevice)
        {
            StatusMessage = null;
            ValidationMessage = "Connect your phone first — Linc needs the phone's display info to recommend settings.";
            return;
        }

        var adbDevice = new AdvancedSharpAdbClient.Models.DeviceData
        {
            Serial = connectedDevice.Serial,
            State = AdvancedSharpAdbClient.Models.DeviceState.Online
        };
        try
        {
            var current = _registry.DesktopMode with
            {
                VirtualDisplayWidth = Width,
                VirtualDisplayHeight = Height,
                VirtualDisplayDpi = Dpi,
                AspectRatioMode = AspectMode
            };
            var recommended = await _desktopModeService.GetRecommendedSettingsAsync(adbDevice, current, CancellationToken.None);
            Width = recommended.VirtualDisplayWidth;
            Height = recommended.VirtualDisplayHeight;
            Dpi = recommended.VirtualDisplayDpi;
            // AspectMode is left as the user chose; the recalc respects it.
            StatusMessage = "Recommended values filled in. Review them, then Save.";
            ValidationMessage = null;
        }
        catch (LincException ex)
        {
            _log.Log(LogLevel.Error, $"Desktop Mode reset-to-recommended error: {ex}");
            ValidationMessage = ex.Message;
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            _log.Log(LogLevel.Error, $"Desktop Mode reset-to-recommended unexpected error: {ex}");
            ValidationMessage = "Linc couldn't read your phone's display info. Make sure the phone is connected and try again.";
            StatusMessage = null;
        }
    }
}
