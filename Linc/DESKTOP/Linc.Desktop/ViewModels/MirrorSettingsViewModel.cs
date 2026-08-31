using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linc.Desktop.Services;
using Microsoft.UI.Dispatching;

namespace Linc.Desktop.ViewModels;

/// <summary>
/// Edits one device's <see cref="MirrorSettings"/> (the normal screen-mirror scrcpy
/// settings). Loaded from <see cref="IDeviceRegistry.Mirror"/> and saved back via
/// <see cref="IDeviceRegistry.SaveMirror"/>. Settings apply to the next mirror start,
/// not a running session — the UI caption (DevicePage.xaml) says so. Validation surfaces
/// plain-language messages via <see cref="ValidationMessage"/>; invalid input does not save.
/// Modelled on <see cref="DesktopModeSettingsViewModel"/>.
/// </summary>
public partial class MirrorSettingsViewModel : ObservableObject
{
    // Pure validation lives on the dependency-free MirrorSettings record
    // (MirrorSettings.ValidateSettingsFields) so the mirrorsettingssim harness can
    // test it without constructing this VM (which needs DispatcherQueue / WinUI).

    private readonly IDeviceRegistry _registry;
    private readonly DispatcherQueue _dispatcher;

    // Snapshot of the loaded settings so IsDirty can compare against them. MaxSize/MaxFps 0
    // mean "use the scrcpy default" — we store the field values verbatim and only validate
    // ranges when the user types a non-zero number (0 is always allowed).
    private MirrorSettings _loaded = new();

    public MirrorSettingsViewModel(IDeviceRegistry registry)
    {
        _registry = registry;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        // Keep this VM's view fresh if the active device changes (per-device settings, D-037).
        _registry.ActiveDeviceChanged += () => _dispatcher.TryEnqueue(Load);
        // Our own Save() calls SaveMirror, which raises MirrorChanged. We skip that
        // self-trigger (via _saving) so Save()'s "Settings saved" StatusMessage survives —
        // otherwise the enqueued Load() would reset StatusMessage to null and wipe it.
        _registry.MirrorChanged += OnMirrorChanged;
        // M11 B2.4: DesktopModeSettings.ForwardAudio feeds AudioConflictWarning below, so a
        // change on THAT side must also re-raise it here — the M6a composed-property trap.
        _registry.DesktopModeChanged += () => _dispatcher.TryEnqueue(RaiseAudioConflictWarning);

        Load();
    }

    [ObservableProperty]
    public partial int MaxSize { get; set; }

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
    public partial int MaxFps { get; set; }

    private string? _crop;
    public string? Crop
    {
        get => _crop;
        set
        {
            if (SetProperty(ref _crop, value))
            {
                RecomputeDirty();
            }
        }
    }

    [ObservableProperty]
    public partial bool StayAwake { get; set; }

    [ObservableProperty]
    public partial bool TurnScreenOff { get; set; }

    [ObservableProperty]
    public partial bool ShowTouches { get; set; }

    [ObservableProperty]
    public partial bool AudioEnabled { get; set; }

    [ObservableProperty]
    public partial int AudioBitRate { get; set; }

    /// <summary>The scrcpy "default" source shows as this sentinel so <see cref="AudioSources"/>
    /// can bind a ComboBox's SelectedItem to a non-null string; converted to/from null (the
    /// record's "use scrcpy's default" value) at the Load/Save boundary only.</summary>
    public const string AudioSourceDefault = "(default)";

    public IReadOnlyList<string> AudioSources { get; } = [AudioSourceDefault, "output", "mic"];

    private string _audioSource = AudioSourceDefault;
    public string AudioSource
    {
        get => _audioSource;
        set
        {
            if (SetProperty(ref _audioSource, value ?? AudioSourceDefault))
            {
                RecomputeDirty();
            }
        }
    }

    /// <summary>M11 B2.4: scrcpy does not arbitrate two simultaneous audio consumers on the same
    /// phone, so if Desktop Mode already has its own audio on, turning this on too means only one
    /// of the two may actually get sound. A plain-language warning, not a block — B2.4 explicitly
    /// forbids building real stream arbitration.</summary>
    public string? AudioConflictWarning => AudioEnabled && _registry.DesktopMode.ForwardAudio
        ? "Desktop Mode already has audio on. Audio usually only reaches one of the two at a time, so whichever you start second may end up silent."
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
    /// MirrorChanged so switching tabs always shows the right phone's settings.</summary>
    public void Load()
    {
        var s = _registry.Mirror;
        _loaded = s;
        _syncing = true;
        try
        {
            MaxSize = s.MaxSize;
            VideoBitRate = s.VideoBitRate;
            MaxFps = s.MaxFps;
            Crop = s.Crop;
            StayAwake = s.StayAwake;
            TurnScreenOff = s.TurnScreenOff;
            ShowTouches = s.ShowTouches;
            AudioEnabled = s.AudioEnabled;
            AudioBitRate = s.AudioBitRate;
            AudioSource = s.AudioSource ?? AudioSourceDefault;
        }
        finally
        {
            _syncing = false;
        }
        ValidationMessage = null;
        StatusMessage = null;
        RecomputeDirty();
    }

    /// <summary>MirrorChanged handler. Skips the reload that our own Save() triggers (see
    /// <see cref="Save"/>) so the "Settings saved" message isn't wiped by the enqueued
    /// Load() resetting StatusMessage. Other MirrorChanged causes — another path saving
    /// mirror settings, or the active device swap observed by the same event — still reload
    /// so this VM never shows stale values.</summary>
    private void OnMirrorChanged()
    {
        if (_saving)
        {
            return;
        }
        _dispatcher.TryEnqueue(Load);
    }

    // Suppress the per-property dirty recompute while Load() pushes values back.
    private bool _syncing;

    // True only while Save() is inside SaveMirror(); OnMirrorChanged ignores that one event.
    // Plain bool (not volatile): Save() exits the flag synchronously before any load it lets
    // through could run, and the enqueued Load() it suppresses would have run on this thread.
    private bool _saving;

    partial void OnMaxSizeChanged(int value) => RecomputeDirty();
    partial void OnMaxFpsChanged(int value) => RecomputeDirty();
    partial void OnStayAwakeChanged(bool value) => RecomputeDirty();
    partial void OnTurnScreenOffChanged(bool value) => RecomputeDirty();
    partial void OnShowTouchesChanged(bool value) => RecomputeDirty();
    partial void OnAudioEnabledChanged(bool value)
    {
        RecomputeDirty();
        RaiseAudioConflictWarning();
    }
    partial void OnAudioBitRateChanged(int value) => RecomputeDirty();

    private void RecomputeDirty()
    {
        if (_syncing)
        {
            return;
        }
        var dirty = MaxSize != _loaded.MaxSize
            || !string.Equals(VideoBitRate, _loaded.VideoBitRate, StringComparison.Ordinal)
            || MaxFps != _loaded.MaxFps
            || !string.Equals(Crop ?? "", _loaded.Crop ?? "", StringComparison.Ordinal)
            || StayAwake != _loaded.StayAwake
            || TurnScreenOff != _loaded.TurnScreenOff
            || ShowTouches != _loaded.ShowTouches
            || AudioEnabled != _loaded.AudioEnabled
            || AudioBitRate != _loaded.AudioBitRate
            || !string.Equals(AudioSource, _loaded.AudioSource ?? AudioSourceDefault, StringComparison.Ordinal);
        IsDirty = dirty;
        // Clear a stale validation message once the user has changed something — the next
        // Save re-runs validation. Don't clear a current failure if nothing changed.
        if (dirty && !string.IsNullOrWhiteSpace(ValidationMessage))
        {
            ValidationMessage = null;
        }
    }

    /// <summary>Validates the editable fields. Returns null when everything is fine, otherwise
    /// a plain-language message. Allows 0 for MaxSize/MaxFps (the "use scrcpy default"
    /// value). Delegates to <see cref="MirrorSettings.ValidateSettingsFields"/> so the
    /// mirrorsettingssim harness can exercise the same rules without the VM.</summary>
    internal static string? ValidateFields(
        int maxSize, string bitRate, int maxFps, string? crop, string? audioSource, int audioBitRate)
        => MirrorSettings.ValidateSettingsFields(maxSize, bitRate, maxFps, crop, audioSource, audioBitRate);

    /// <summary>Public Validate used by the Save command; delegates to the pure helper.</summary>
    public string? Validate() => ValidateFields(
        MaxSize, VideoBitRate, MaxFps, Crop,
        AudioSource == AudioSourceDefault ? null : AudioSource, AudioBitRate);

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
        var settings = new MirrorSettings(
            MaxSize: MaxSize,
            VideoBitRate: VideoBitRate,
            MaxFps: MaxFps,
            Crop: string.IsNullOrWhiteSpace(Crop) ? null : Crop,
            StayAwake: StayAwake,
            TurnScreenOff: TurnScreenOff,
            ShowTouches: ShowTouches,
            AudioEnabled: AudioEnabled,
            AudioBitRate: AudioBitRate,
            AudioSource: AudioSource == AudioSourceDefault ? null : AudioSource);
        _saving = true;
        try
        {
            _registry.SaveMirror(settings);
        }
        finally
        {
            _saving = false;
        }
        _loaded = settings;
        RecomputeDirty();
        StatusMessage = "Settings saved. They apply the next time you start mirroring.";
    }

    [RelayCommand]
    private void Reset()
    {
        // D-054: the record's positional defaults (new MirrorSettings()) are editor blanks
        // (MaxSize 0 = native), NOT the persisted substitute. BalancedDefaults is the
        // byte-for-byte "Balanced" preset and what DeviceRegistry.Mirror returns for a
        // never-touched device — so resets must land here too, otherwise the editor shows
        // blanks the registry would never store.
        var defaults = MirrorSettings.BalancedDefaults;
        _syncing = true;
        try
        {
            MaxSize = defaults.MaxSize;
            VideoBitRate = defaults.VideoBitRate;
            MaxFps = defaults.MaxFps;
            Crop = defaults.Crop;
            StayAwake = defaults.StayAwake;
            TurnScreenOff = defaults.TurnScreenOff;
            ShowTouches = defaults.ShowTouches;
            AudioEnabled = defaults.AudioEnabled;
            AudioBitRate = defaults.AudioBitRate;
            AudioSource = defaults.AudioSource ?? AudioSourceDefault;
        }
        finally
        {
            _syncing = false;
        }
        RecomputeDirty();
        StatusMessage = "Reset to defaults. Review them, then Save to persist.";
        ValidationMessage = null;
    }
}
