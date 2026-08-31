using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Linc.Desktop.Protocol;

namespace Linc.Desktop.Services;

public interface IPcControlService
{
    /// <summary>Begin answering the phone's <c>pc.control</c> / <c>pc.state.get</c> requests.</summary>
    void Attach();
}

/// <summary>
/// Answers the phone's quick controls for this PC (v17, M4b, D-042): lock, sleep, shutdown,
/// restart, volume and display brightness. Request/reply, unlike <see cref="PcInputService"/> —
/// every action here can fail for a reason the user needs to see (PROTOCOL.md v17).
///
/// <para><b>Safety.</b> This class is the only place a <c>pc.control</c> action can actually
/// reach a Win32/COM/WMI call — the structural validation it defers to
/// (<see cref="PcControlValidator"/>) has none of that in its path by construction, which is
/// what lets <c>tools\pccontrolsim</c> exercise the safety gate without ever touching the OS.</para>
///
/// <para><b>Never trust the phone to have clamped.</b> Every value is re-validated here even
/// though the phone validates too (PROTOCOL.md: "Validate desktop-side; never trust the phone
/// to have clamped").</para>
/// </summary>
public sealed class PcControlService(IConnectionManager connection, ILogService log) : IPcControlService
{
    private bool _subscribed;

    public void Attach()
    {
        if (_subscribed) return;
        _subscribed = true;
        connection.CompanionMessageReceived += OnCompanionMessage;
    }

    private void OnCompanionMessage(Envelope envelope)
    {
        switch (envelope.Type)
        {
            case MessageType.PcControl:
                _ = HandleControlAsync(envelope);
                break;

            case MessageType.PcStateGet:
                _ = SendStateAsync(envelope.Id);
                break;
        }
    }

    private async Task HandleControlAsync(Envelope envelope)
    {
        var payload = envelope.Payload;
        string? action = (string?)payload["action"];
        int? level = (int?)payload["level"];
        int? step = (int?)payload["step"];
        bool? on = (bool?)payload["on"];
        bool? confirm = (bool?)payload["confirm"];

        var error = PcControlValidator.ValidateControl(action, level, step, on, confirm)
            ?? Execute(action!, level, step, on);

        try
        {
            if (error is null)
            {
                await connection.SendToPhoneAsync(
                    Envelope.Create(MessageType.Ok, replyTo: envelope.Id), CancellationToken.None);
            }
            else
            {
                await connection.SendToPhoneAsync(
                    Envelope.Create(MessageType.Error, new JsonObject
                    {
                        ["code"] = error,
                        ["message"] = PlainMessage(action, error),
                    }, replyTo: envelope.Id),
                    CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            log.Log(LogLevel.Warn, $"Couldn't reply to pc.control '{action}': {ex.Message}");
        }
    }

    private async Task SendStateAsync(string replyTo)
    {
        try
        {
            await connection.SendToPhoneAsync(
                Envelope.Create(MessageType.PcState, BuildStatePayload(), replyTo), CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.Log(LogLevel.Warn, $"Couldn't send pc.state: {ex.Message}");
        }
    }

    private static string PlainMessage(string? action, string code) => code switch
    {
        "needs-confirm" => "This is a destructive action and needs confirmation.",
        "unsupported" when action == "brightness.set" =>
            "This PC's screen brightness can't be controlled remotely.",
        "unsupported" => "This PC can't do that right now.",
        "denied" => "Windows refused that request.",
        _ => "That request wasn't understood.",
    };

    // ---- executing a validated action ---------------------------------------

    /// <summary>
    /// Attempts the already-validated action. Returns null on success, otherwise one of
    /// <c>"unsupported"</c> / <c>"denied"</c> / <c>"internal"</c>. Never called with an action
    /// <see cref="PcControlValidator"/> rejected.
    /// </summary>
    private string? Execute(string action, int? level, int? step, bool? on)
    {
        try
        {
            return action switch
            {
                "lock" => LockWorkStation() ? null : "denied",
                "sleep" => SetSuspendState(false, false, false) ? null : "denied",
                "shutdown" => PowerDown(restart: false),
                "restart" => PowerDown(restart: true),
                "volume.up" => VolumeStep(step ?? 1),
                "volume.down" => VolumeStep(-(step ?? 1)),
                "volume.set" => VolumeSet(level!.Value),
                "volume.mute" => VolumeMute(on),
                "brightness.set" => SetBrightness(level!.Value),
                _ => "internal",
            };
        }
        catch (Exception ex)
        {
            log.Log(LogLevel.Warn, $"pc.control '{action}' failed: {ex.Message}");
            return "internal";
        }
    }

    private JsonObject BuildStatePayload()
    {
        var (volumeOk, scalar, muted) = TryGetVolume();
        var canBrightness = CanControlBrightness();
        int brightness = 0;
        var hasBrightness = canBrightness && TryGetBrightness(out brightness);
        JsonNode? brightnessNode = hasBrightness ? JsonValue.Create(brightness) : null;

        return new JsonObject
        {
            ["volume"] = volumeOk ? (int)Math.Round(scalar * 100) : 0,
            ["muted"] = volumeOk && muted,
            ["brightness"] = brightnessNode,
            ["canBrightness"] = canBrightness,
            ["canSleep"] = TryGetCanSleep(),
            ["canShutdown"] = IsPwrShutdownAllowed(),
        };
    }

    // ---- power: lock / sleep / shutdown / restart ---------------------------

    /// <summary>
    /// Shutdown/restart via <c>ExitWindowsEx</c>, gated on <c>SE_SHUTDOWN_NAME</c> explicitly
    /// enabled on this process's token first — a normal user process may enable that privilege
    /// but does not hold it by default, which is the likely cause of a bare <c>ExitWindowsEx</c>
    /// failing with <c>denied</c> (PROTOCOL.md v17, A3). Deliberately does NOT set
    /// <c>EWX_FORCEIFHUNG</c>: a hung app should still get to prompt "save your work?" rather
    /// than being killed outright on the owner's say-so from the phone.
    /// </summary>
    private static string? PowerDown(bool restart)
    {
        if (!EnableShutdownPrivilege()) return "denied";

        uint flags = restart ? EWX_REBOOT : EWX_SHUTDOWN;
        uint reason = SHTDN_REASON_FLAG_PLANNED | SHTDN_REASON_MAJOR_APPLICATION;
        return ExitWindowsEx(flags, reason) ? null : "denied";
    }

    private static bool EnableShutdownPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
        {
            return false;
        }
        try
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid))
            {
                return false;
            }
            var privileges = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SE_PRIVILEGE_ENABLED },
            };
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
            {
                return false;
            }
            // AdjustTokenPrivileges can return true while quietly not assigning the privilege
            // (MSDN: check GetLastError for ERROR_NOT_ALL_ASSIGNED) — a normal user process
            // enabling SeShutdownPrivilege should succeed, but if it didn't, say so honestly
            // as "denied" rather than proceeding to a call that will just fail anyway.
            return Marshal.GetLastWin32Error() != ErrorNotAllAssigned;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    // ---- volume: Core Audio IAudioEndpointVolume ----------------------------

    private static (bool Ok, float Scalar, bool Muted) TryGetVolume()
    {
        try
        {
            var endpoint = GetDefaultEndpoint();
            if (endpoint is null) return (false, 0, false);
            endpoint.GetMasterVolumeLevelScalar(out float scalar);
            endpoint.GetMute(out bool muted);
            return (true, scalar, muted);
        }
        catch
        {
            return (false, 0, false);
        }
    }

    private static string? VolumeStep(int steps)
    {
        try
        {
            var endpoint = GetDefaultEndpoint();
            if (endpoint is null) return "unsupported";
            var context = Guid.Empty;
            for (int i = 0; i < Math.Abs(steps); i++)
            {
                if (steps > 0) endpoint.VolumeStepUp(ref context);
                else endpoint.VolumeStepDown(ref context);
            }
            return null;
        }
        catch
        {
            return "denied";
        }
    }

    private static string? VolumeSet(int level)
    {
        try
        {
            var endpoint = GetDefaultEndpoint();
            if (endpoint is null) return "unsupported";
            var context = Guid.Empty;
            endpoint.SetMasterVolumeLevelScalar(Math.Clamp(level, 0, 100) / 100f, ref context);
            return null;
        }
        catch
        {
            return "denied";
        }
    }

    private static string? VolumeMute(bool? on)
    {
        try
        {
            var endpoint = GetDefaultEndpoint();
            if (endpoint is null) return "unsupported";
            endpoint.GetMute(out bool currentlyMuted);
            var context = Guid.Empty;
            endpoint.SetMute(on ?? !currentlyMuted, ref context);
            return null;
        }
        catch
        {
            return "denied";
        }
    }

    private static IAudioEndpointVolume? GetDefaultEndpoint()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device);
        if (device is null) return null;

        var iid = typeof(IAudioEndpointVolume).GUID;
        device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var iface);
        return iface as IAudioEndpointVolume;
    }

    // ---- brightness: WMI WmiMonitorBrightness / WmiMonitorBrightnessMethods -
    // DDC/CI for external monitors is deliberately out of scope (PROTOCOL.md v17, A3) — most
    // desktop monitors won't answer it, and it's a rabbit hole for a "quick controls" feature.

    private static bool CanControlBrightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            using var results = searcher.Get();
            foreach (ManagementBaseObject _ in results) return true;
        }
        catch
        {
            // No WMI monitor-brightness namespace on this machine (common on desktop PCs).
        }
        return false;
    }

    private static bool TryGetBrightness(out int brightness)
    {
        brightness = 0;
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            using var results = searcher.Get();
            foreach (ManagementBaseObject result in results)
            {
                brightness = Convert.ToInt32(result["CurrentBrightness"]);
                return true;
            }
        }
        catch
        {
            // Unreadable — pc.state reports brightness: null, which is the wire's contract for this.
        }
        return false;
    }

    private static string? SetBrightness(int level)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            using var results = searcher.Get();
            foreach (ManagementObject result in results)
            {
                result.InvokeMethod("WmiSetBrightness", [(uint)0, (byte)Math.Clamp(level, 0, 100)]);
                return null;
            }
            return "unsupported";
        }
        catch
        {
            return "denied";
        }
    }

    // ---- Win32/COM interop ---------------------------------------------------

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    /// <summary>Whether Windows would currently allow a shutdown request — reflects policy
    /// locks and machine capability without attempting the action (used for <c>pc.state</c>'s
    /// <c>canShutdown</c>). <c>canSleep</c> uses <see cref="TryGetCanSleep"/> instead — see its
    /// doc comment for why.</summary>
    [DllImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool IsPwrShutdownAllowed();

    /// <summary>Whether Windows would currently allow a sleep request (M12i, replacing
    /// <c>IsPwrSuspendAllowed()</c>). That export answers for legacy ACPI S3 only and was
    /// runtime-verified to return <c>false</c> on this dev machine while Sleep works fine from
    /// its own Start menu — a Modern Standby (S0 low-power idle) machine. <c>GetPwrCapabilities</c>
    /// reports both mechanisms in one call: <c>SystemS3</c> for legacy sleep, <c>AoAc</c> ("Always
    /// On, Always Connected") for Modern Standby. The pure OR is <see cref="PcPowerCapabilities.CanSleep"/>,
    /// tested in isolation by <c>tools\pccontrolsim</c> without touching Win32.</summary>
    private static bool TryGetCanSleep()
    {
        try
        {
            return GetPwrCapabilities(out var caps) && PcPowerCapabilities.CanSleep(caps.SystemS3, caps.AoAc);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetPwrCapabilities(out SYSTEM_POWER_CAPABILITIES capabilities);

    /// <summary>Only the two fields <see cref="TryGetCanSleep"/> needs, at their real offsets —
    /// <c>Size = 76</c> matches the full native struct (verified against the Windows
    /// 10.0.26100.0 SDK's <c>winnt.h</c>) so <c>GetPwrCapabilities</c> writes into a
    /// correctly-sized buffer without this type naming every one of the struct's other members.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 76)]
    private struct SYSTEM_POWER_CAPABILITIES
    {
        [FieldOffset(5)]
        [MarshalAs(UnmanagedType.U1)]
        public bool SystemS3;

        [FieldOffset(20)]
        [MarshalAs(UnmanagedType.U1)]
        public bool AoAc;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ExitWindowsEx(uint flags, uint reason);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? host, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
    private const uint TOKEN_QUERY = 0x8;
    private const uint SE_PRIVILEGE_ENABLED = 0x2;
    private const uint EWX_SHUTDOWN = 0x00000001;
    private const uint EWX_REBOOT = 0x00000002;
    private const uint SHTDN_REASON_MAJOR_APPLICATION = 0x00040000;
    private const uint SHTDN_REASON_FLAG_PLANNED = 0x80000000;
    private const int ErrorNotAllAssigned = 1300;
    private const uint ClsctxAll = 23; // CLSCTX_INPROC_SERVER|INPROC_HANDLER|LOCAL_SERVER|REMOTE_SERVER

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID_AND_ATTRIBUTES Privileges; }

    private enum EDataFlow { Render = 0, Capture = 1, All = 2 }
    private enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice? endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object? ppInterface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint channelCount);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
        [PreserveSig] int VolumeStepUp(ref Guid eventContext);
        [PreserveSig] int VolumeStepDown(ref Guid eventContext);
        [PreserveSig] int QueryHardwareSupport(out uint hardwareSupportMask);
        [PreserveSig] int GetVolumeRange(out float volumeMinDb, out float volumeMaxDb, out float volumeIncrementDb);
    }
}
