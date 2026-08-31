using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linc.Desktop.Services;
using Microsoft.UI.Dispatching;

namespace Linc.Desktop.ViewModels;

/// <summary>
/// M5c-2 (D-055): the v15 display-control widgets — rotation picker + brightness
/// auto-toggle + 0–100 brightness slider. Lives on the Device page's Tools card.
/// Gated on the negotiated protocol version (>= 15); when unsupported the widgets
/// are disabled and a plain-language reason is shown rather than hidden.
///
/// Status fields (rotationMode / brightnessAuto / brightnessLevel) arrive via
/// <see cref="IConnectionSupervisor.StatusUpdated"/>. While any value is null
/// (a v14 phone, or a v15 phone before its first status snapshot), the UI shows
/// "unknown" and sends nothing — only a deliberate user change triggers a frame.
/// </summary>
public partial class DisplayControlViewModel : ObservableObject
{
    private readonly IConnectionSupervisor _supervisor;
    private readonly IConnectionManager _connection;
    private readonly ILogService _log;
    private readonly DispatcherQueue _dispatcher;

    public IReadOnlyList<string> RotationOptions { get; } = new[] { "auto", "portrait", "landscape" };

    public DisplayControlViewModel(
        IConnectionSupervisor supervisor,
        IConnectionManager connection,
        ILogService log)
    {
        _supervisor = supervisor;
        _connection = connection;
        _log = log;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _supervisor.StateChanged += () => _dispatcher.TryEnqueue(RefreshSupport);
        _supervisor.StatusUpdated += status => _dispatcher.TryEnqueue(() => SeedFromStatus(status));
        RefreshSupport();
    }

    // ---- Rotation ----

    private string? _rotationMode;
    /// <summary>The current rotation mode ("auto"/"portrait"/"landscape") or null when unknown.</summary>
    public string? RotationMode
    {
        get => _rotationMode;
        private set
        {
            if (SetProperty(ref _rotationMode, value))
            {
                OnPropertyChanged(nameof(HasRotation));
                OnPropertyChanged(nameof(RotationStatusText));
            }
        }
    }

    public bool HasRotation => RotationMode is not null;

    public string RotationStatusText => RotationMode switch
    {
        "auto" => "Auto-rotate is on.",
        "portrait" => "Locked to portrait.",
        "landscape" => "Locked to landscape.",
        null => "Unknown.",
        _ => $"Currently in an unexpected mode ('{RotationMode}').",
    };

    [RelayCommand]
    private async Task SetRotationAsync(string? mode)
    {
        if (mode is null || !RotationOptions.Contains(mode))
        {
            return; // guard against XAML binding nulls
        }
        ErrorMessage = null;
        RotationMode = mode; // optimistic; the phone's next status will confirm
        try
        {
            await _connection.SetDisplayRotationAsync(mode, CancellationToken.None);
        }
        catch (LincException ex)
        {
            // Phone's not-granted text already names the right Settings screen — pass through.
            _log.Log(LogLevel.Warn, $"Display rotation set failed: {ex}");
            ErrorMessage = ex.Message;
        }
    }

    // ---- Brightness ----

    private bool? _brightnessAuto;
    public bool? BrightnessAuto
    {
        get => _brightnessAuto;
        private set
        {
            if (SetProperty(ref _brightnessAuto, value))
            {
                OnPropertyChanged(nameof(HasBrightnessAuto));
                OnPropertyChanged(nameof(IsBrightnessSliderEnabled));
                OnPropertyChanged(nameof(BrightnessAutoStatusText));
            }
        }
    }

    private int? _brightnessLevel;
    public int? BrightnessLevel
    {
        get => _brightnessLevel;
        private set
        {
            if (SetProperty(ref _brightnessLevel, value))
            {
                OnPropertyChanged(nameof(HasBrightnessLevel));
                OnPropertyChanged(nameof(BrightnessLevelStatusText));
            }
        }
    }

    public bool HasBrightnessAuto => BrightnessAuto is not null;
    public bool HasBrightnessLevel => BrightnessLevel is not null;
    // The slider is only meaningful when we know adaptive is OFF — the wire ignores the
    // level when auto is true, and a null auto means we don't know yet.
    public bool IsBrightnessSliderEnabled => BrightnessAuto == false;

    // x:Bind shims — WinUI's ToggleSwitch.IsOn and Slider.Value both reject nullable
    // types ("Cannot directly bind type 'System.Nullable(System.Boolean)' to
    // 'System.Boolean'"). The UI surfaces null through these non-nullable shims by
    // returning false / 0 when the phone hasn't sent a value yet; the matching
    // Is*Enabled property is bound to the nullable so the control is disabled until
    // the phone's first status arrives.
    public bool BrightnessAutoToggleOn => BrightnessAuto == true;
    public double BrightnessLevelSlider => BrightnessLevel ?? 0;

    public string BrightnessAutoStatusText => BrightnessAuto switch
    {
        true => "Adaptive brightness is on.",
        false => "Adaptive brightness is off — the slider controls the level.",
        null => "Adaptive-brightness state unknown.",
    };

    public string BrightnessLevelStatusText => BrightnessLevel is { } level
        ? $"Currently at {level}%."
        : "Brightness level unknown.";

    /// <summary>
    /// Toggles adaptive brightness. Always sends one frame — there is nothing to debounce
    /// (a tap is a discrete event).
    /// </summary>
    [RelayCommand]
    private async Task SetBrightnessAutoAsync(bool? auto)
    {
        if (auto is null)
        {
            return;
        }
        ErrorMessage = null;
        var previous = BrightnessAuto;
        BrightnessAuto = auto;
        if (auto == true)
        {
            // Adaptive ON: send auto:true with no level. The phone ignores level there.
            try
            {
                await _connection.SetDisplayBrightnessAsync(true, null, CancellationToken.None);
            }
            catch (LincException ex)
            {
                BrightnessAuto = previous;
                _log.Log(LogLevel.Warn, $"Display brightness auto set failed: {ex}");
                ErrorMessage = ex.Message;
            }
            return;
        }
        // Adaptive OFF: send the current slider value, or refuse if we don't have one.
        if (BrightnessLevel is not { } level)
        {
            BrightnessAuto = previous;
            ErrorMessage = "Linc can't turn adaptive brightness off without a known level. " +
                "Drag the slider to a value first, then try again.";
            return;
        }
        try
        {
            await _connection.SetDisplayBrightnessAsync(false, level, CancellationToken.None);
        }
        catch (LincException ex)
        {
            BrightnessAuto = previous;
            _log.Log(LogLevel.Warn, $"Display brightness auto set failed: {ex}");
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>
    /// Brightness slider drag. Drag events fire per pixel; the link would flood.
    /// Coalesce them with a 250 ms dispatcher timer — only the last drag value within
    /// that window is sent. While the timer is armed we are "pending" and the UI may
    /// show that; the next status snapshot confirms what the phone actually accepted.
    /// </summary>
    [RelayCommand]
    private void SetBrightnessLevel(int? level)
    {
        if (level is null)
        {
            return;
        }
        // Update the slider immediately for a responsive feel; the actual wire send
        // is debounced.
        BrightnessLevel = level;
        if (BrightnessAuto != false)
        {
            // Adaptive is on (or unknown) — the slider is disabled. Ignore stray drag
            // events (some controls still raise them even when disabled).
            return;
        }
        _pendingLevel = level;
        if (_debounceTimer is null)
        {
            _debounceTimer = _dispatcher.CreateTimer();
            _debounceTimer.Interval = TimeSpan.FromMilliseconds(250);
            _debounceTimer.IsRepeating = false;
            _debounceTimer.Tick += OnDebounceTick;
        }
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private DispatcherQueueTimer? _debounceTimer;
    private int? _pendingLevel;

    private async void OnDebounceTick(DispatcherQueueTimer sender, object? args)
    {
        var level = _pendingLevel;
        if (level is null)
        {
            return;
        }
        _pendingLevel = null;
        try
        {
            await _connection.SetDisplayBrightnessAsync(false, level, CancellationToken.None);
        }
        catch (LincException ex)
        {
            _log.Log(LogLevel.Warn, $"Display brightness level set failed: {ex}");
            ErrorMessage = ex.Message;
        }
    }

    // ---- Support gate + status messaging ----

    private void RefreshSupport()
    {
        var v = _supervisor.Device?.Companion.V;
        OnPropertyChanged(nameof(IsSupported));
        OnPropertyChanged(nameof(UnsupportedReason));
        // A connection-state change can re-enable the widgets; refresh support flags.
        OnPropertyChanged(nameof(CanChangeRotation));
        OnPropertyChanged(nameof(CanChangeBrightness));
    }

    public bool IsSupported => DisplayPayload.IsSupported(_supervisor.Device?.Companion.V);

    public string UnsupportedReason => DisplayPayload.UnsupportedReason(_supervisor.Device?.Companion.V);

    /// <summary>True when the widget should be enabled: supported AND connected.</summary>
    public bool CanChangeRotation => IsSupported && _supervisor.State == LinkState.Connected;

    /// <summary>True when the widget should be enabled: supported AND connected.</summary>
    public bool CanChangeBrightness => IsSupported && _supervisor.State == LinkState.Connected;

    /// <summary>Top-of-section caption explaining the unknown state until the first status arrives.</summary>
    public string StatusText => !IsSupported
        ? UnsupportedReason
        : RotationMode is null && BrightnessAuto is null && BrightnessLevel is null
            ? "Waiting for the phone's first status…"
            : "";

    // ---- Error surface ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    private void SeedFromStatus(DeviceStatus status)
    {
        // The phone is the source of truth on rotation / brightness — overwrite whatever
        // the user or the optimistic setter wrote. A user mid-drag is unaffected: the
        // slider only commits after 250 ms of quiet, by which time the phone's snapshot
        // has already arrived or the drag is still in flight.
        if (status.RotationMode is { } rotation && RotationOptions.Contains(rotation))
        {
            RotationMode = rotation;
        }
        if (status.BrightnessAuto is { } auto)
        {
            BrightnessAuto = auto;
        }
        if (status.BrightnessLevel is { } level && level is >= 0 and <= 100)
        {
            BrightnessLevel = level;
        }
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanChangeRotation));
        OnPropertyChanged(nameof(CanChangeBrightness));
        OnPropertyChanged(nameof(IsBrightnessSliderEnabled));
    }
}
