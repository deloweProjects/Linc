using System.Text.Json.Nodes;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace Linc.Desktop.Services;

/// <summary>
/// The phone's sound on this PC the way Windows does it itself: the PC becomes a Bluetooth
/// speaker for the phone (A2DP sink, <see cref="AudioPlaybackConnection"/>, Windows 10 2004+).
/// The phone then plays to the PC like to any Bluetooth speaker: no recording prompt, nothing
/// captured, and Android keeps picture and sound together on its own. The owner, about the
/// recording route it replaces: "starts a recording. Bro whyyy, there's obviously better ways".
/// Needs the phone paired with the PC over Bluetooth once; Linc says so when it isn't.
/// </summary>
public sealed class PcBluetoothAudio(ILogService log)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AudioPlaybackConnection? _open;
    private string? _device;
    private string? _error;

    /// <summary>Raised when the connection opens, closes or fails, from any thread.</summary>
    public event Action? Changed;

    // A guard the platform analyzer understands (CA1416): AudioPlaybackConnection is Windows 10
    // 2004 (19041) and up, while Linc runs from 1809 (17763).
    [System.Runtime.Versioning.SupportedOSPlatformGuard("windows10.0.19041.0")]
    public static bool Supported =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) &&
        Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.Media.Audio.AudioPlaybackConnection");

    public bool IsOn => _open is not null;
    public string? Device => _device;
    public string? Error => _error;

    /// <summary>{on, supported, device?, error?}: error is not-paired, unsupported or Windows' own status.</summary>
    public JsonObject State() => new()
    {
        ["on"] = IsOn,
        ["supported"] = Supported,
        ["device"] = _device,
        ["error"] = _error,
    };

    /// <summary>
    /// Open the Bluetooth sound connection to the phone (or close it). <paramref name="names"/>
    /// are what the phone may be called in Bluetooth (its device name, its model): the first
    /// paired phone matching one wins, else the only one, else the first.
    /// </summary>
    public async Task SetAsync(bool on, IReadOnlyList<string> names)
    {
        await _gate.WaitAsync();
        try
        {
            if (!on)
            {
                if (Supported) CloseLocked("stopped"); // nothing can be open where it isn't
                _error = null;
                return;
            }
            if (!Supported)
            {
                _error = "unsupported";
                return;
            }
            var devices = await DeviceInformation.FindAllAsync(AudioPlaybackConnection.GetDeviceSelector());
            if (devices.Count == 0)
            {
                _error = "not-paired";
                log.Log(LogLevel.Info, "Phone sound over Bluetooth: no phone is paired with this PC over Bluetooth yet");
                return;
            }
            var pick = Pick(devices, names);
            if (_open is not null && _device == pick.Name)
            {
                _error = null;
                return; // already playing from that phone
            }
            CloseLocked("switching phones");
            var connection = AudioPlaybackConnection.TryCreateFromId(pick.Id);
            if (connection is null)
            {
                _error = "failed";
                log.Log(LogLevel.Warn, $"Phone sound over Bluetooth: Windows wouldn't make a connection to {pick.Name}");
                return;
            }
            connection.StateChanged += OnStateChanged;
            await connection.StartAsync();
            var result = await connection.OpenAsync();
            if (result.Status != AudioPlaybackConnectionOpenResultStatus.Success)
            {
                connection.StateChanged -= OnStateChanged;
                connection.Dispose();
                _error = result.Status.ToString();
                log.Log(LogLevel.Warn, $"Phone sound over Bluetooth: couldn't connect to {pick.Name} ({result.Status}{(result.ExtendedError is { } e ? $", 0x{e.HResult:X8}" : "")})");
                return;
            }
            _open = connection;
            _device = pick.Name;
            _error = null;
            log.Log(LogLevel.Info, $"Phone sound over Bluetooth: {pick.Name} now plays on this PC (Windows is its Bluetooth speaker)");
        }
        catch (Exception ex)
        {
            _error = "failed";
            log.Log(LogLevel.Warn, $"Phone sound over Bluetooth failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    private static DeviceInformation Pick(IReadOnlyList<DeviceInformation> devices, IReadOnlyList<string> names)
    {
        foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            var exact = devices.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }
        foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            var near = devices.FirstOrDefault(d => d.Name.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                                                   name.Contains(d.Name, StringComparison.OrdinalIgnoreCase));
            if (near is not null) return near;
        }
        return devices[0];
    }

    /// <summary>The phone turned Bluetooth off, walked away, or picked another speaker.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041.0")]
    private void OnStateChanged(AudioPlaybackConnection sender, object args)
    {
        if (sender.State != AudioPlaybackConnectionState.Closed || !ReferenceEquals(sender, _open)) return;
        _gate.Wait();
        try
        {
            if (ReferenceEquals(sender, _open)) CloseLocked("the phone disconnected");
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041.0")]
    private void CloseLocked(string why)
    {
        if (_open is not { } open) return;
        _open = null;
        open.StateChanged -= OnStateChanged;
        try { open.Dispose(); } catch { }
        log.Log(LogLevel.Info, $"Phone sound over Bluetooth: {_device ?? "the phone"} stopped playing here ({why})");
        _device = null;
    }
}
