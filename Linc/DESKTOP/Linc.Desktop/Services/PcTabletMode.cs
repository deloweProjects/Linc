using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Linc.Desktop.Protocol;
using Microsoft.Win32;

namespace Linc.Desktop.Services;

/// <summary>
/// Windows tablet mode, switched from PC Remote's bubble (v22, D-074).
///
/// <para><b>Windows 11</b> has no tablet-mode switch any more: its tablet layout (touch-spaced
/// taskbar, bigger targets) follows the hardware posture flag <c>ConvertibleSlateMode</c> under
/// <c>HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl</c> (0 = slate/tablet, 1 = laptop),
/// which a 2-in-1's hinge sensor normally sets. Linc writes that flag — machine-wide, so through
/// one elevated <c>reg.exe</c> (a UAC prompt on the PC) — and broadcasts the
/// <c>ConvertibleSlateMode</c> setting change the way the sensor driver does. Windows only acts
/// on it on touch-capable convertibles; on a PC without a touch screen it usually changes nothing
/// visible, and the phone is told so (<c>touch: false</c>).</para>
///
/// <para><b>Windows 10</b> still has real tablet mode, per user and without admin:
/// <c>HKCU\…\ImmersiveShell\TabletMode</c> plus the <c>UserInteractionMode</c> broadcast.</para>
/// </summary>
public sealed class PcTabletMode(IConnectionManager connection, ILogService log)
{
    private const string SlateKey = @"SYSTEM\CurrentControlSet\Control\PriorityControl";
    private const string ShellKey = @"Software\Microsoft\Windows\CurrentVersion\ImmersiveShell";
    private const int ERROR_CANCELLED = 1223;

    public static bool Windows11 => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>The value Windows 11's posture flag takes for tablet (true) or laptop (false).</summary>
    public static int SlateValue(bool tablet) => tablet ? 0 : 1;

    /// <summary>Whether Windows reports a touch screen — without one it won't show its tablet layout.</summary>
    public static bool HasTouch => GetSystemMetrics(SM_MAXIMUMTOUCHES) > 0;

    /// <summary>What Windows currently has: tablet posture (11) or tablet mode (10).</summary>
    public static bool IsOn()
    {
        try
        {
            if (Windows11)
            {
                using var key = Registry.LocalMachine.OpenSubKey(SlateKey);
                return key?.GetValue("ConvertibleSlateMode") is int v && v == SlateValue(true);
            }
            using var shell = Registry.CurrentUser.OpenSubKey(ShellKey);
            return shell?.GetValue("TabletMode") is int t && t == 1;
        }
        catch { return false; }
    }

    public async Task SetAsync(bool on)
    {
        string? error = null;
        try
        {
            error = Windows11 ? await SetWindows11Async(on) : SetWindows10(on);
            if (error is null)
            {
                log.Log(LogLevel.Info, $"Tablet mode {(on ? "on" : "off")} (Windows {(Windows11 ? "11 posture flag" : "10 tablet mode")})"
                    + (Windows11 && !HasTouch ? " — this PC has no touch screen, so Windows may not change its layout" : ""));
            }
        }
        catch (Exception ex) { error = ex.Message; }
        if (error is not null) log.Log(LogLevel.Warn, $"Tablet mode: {error}");
        await SendStateAsync(error);
    }

    public Task SendStateAsync(string? error = null)
    {
        var payload = new JsonObject
        {
            ["on"] = IsOn(),
            ["touch"] = HasTouch,
            ["windows"] = Windows11 ? "11" : "10",
        };
        if (error is not null) payload["error"] = error;
        return connection.SendToPhoneAsync(Envelope.Create(MessageType.PcTabletModeState, payload), CancellationToken.None);
    }

    /// <summary>Write the posture flag elevated, then announce the change. Null on success.</summary>
    private static async Task<string?> SetWindows11Async(bool on)
    {
        var args = $@"add ""HKLM\{SlateKey}"" /v ConvertibleSlateMode /t REG_DWORD /d {SlateValue(on)} /f";
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo("reg.exe", args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            return "Windows' permission prompt was declined on the PC.";
        }
        if (process is null) return "Windows didn't start the change.";
        using (process)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { return "Approve the Windows prompt on the PC (it may be flashing in the taskbar)."; }
            if (process.ExitCode != 0) return $"Windows refused the change (reg.exe exit {process.ExitCode}).";
        }
        Broadcast("ConvertibleSlateMode");
        return null;
    }

    private static string? SetWindows10(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(ShellKey);
        key.SetValue("TabletMode", on ? 1 : 0, RegistryValueKind.DWord);
        Broadcast("UserInteractionMode");
        return null;
    }

    /// <summary>Tell every top-level window a setting changed, as the posture driver does.</summary>
    private static void Broadcast(string setting)
    {
        IntPtr text = Marshal.StringToHGlobalUni(setting);
        try { SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, text, SMTO_ABORTIFHUNG, 3000, out _); }
        finally { Marshal.FreeHGlobal(text); }
    }

    private const int SM_MAXIMUMTOUCHES = 95;
    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeoutMs, out IntPtr result);
}
