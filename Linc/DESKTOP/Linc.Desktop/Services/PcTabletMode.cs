using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Linc.Desktop.Protocol;
using Microsoft.Win32;

namespace Linc.Desktop.Services;

/// <summary>
/// "Tablet mode" from PC Remote's bubble (v22, D-074): make a PC with no touch screen friendly to
/// fingers on the phone.
///
/// <para><b>Not Windows' own tablet mode.</b> Windows 11 only shows its tablet layout on touch
/// 2-in-1s, and switching its posture flag needs admin — a UAC prompt on the secure desktop that
/// broke the mirror's capture every time (the owner: "i reconnect each time … the point of this is
/// to help no touch pc be touch friendly"). So Linc does it itself, as the signed-in user, with no
/// prompt:</para>
/// <list type="bullet">
/// <item><b>Bigger everything</b>: the mirrored display's scale goes up two steps (e.g. 150 % →
/// 200 %), through the same per-display DPI setting Settings → Display → Scale changes, applied
/// live. Buttons, text and title bars all grow, so they are big enough for a fingertip.</item>
/// <item><b>Touch spacing</b> in File Explorer (compact view off).</item>
/// </list>
/// <para>The originals are written to disk before anything changes and put back when Tablet mode
/// is switched off, when the phone leaves PC Remote, when Linc exits, and on the next start after
/// a crash (<see cref="RestorePending"/>).</para>
/// </summary>
public sealed class PcTabletMode(IConnectionManager connection, ILogService log)
{
    /// <summary>Windows' scale steps, in percent; DisplayConfig speaks in steps from the recommended one.</summary>
    internal static readonly int[] Scales = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];
    private const int StepsUp = 2;
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    private static readonly object Gate = new();

    private static string RestoreFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Linc", "tabletmode-restore.json");

    public static bool IsOn => File.Exists(RestoreFile);

    /// <summary>The scale a display at <paramref name="current"/> % goes to: two steps up, within its maximum.</summary>
    public static int TargetScale(int current, int maximum)
    {
        int i = Array.IndexOf(Scales, current);
        if (i < 0) return current;
        int target = Scales[Math.Min(i + StepsUp, Scales.Length - 1)];
        return Math.Min(target, maximum);
    }

    public async Task SetAsync(bool on, string? displayName)
    {
        string? error = null;
        int? scale = null;
        try
        {
            if (on) scale = await Task.Run(() => TurnOn(displayName));
            else await Task.Run(() => RestorePending(log));
        }
        catch (Exception ex)
        {
            error = ex.Message;
            log.Log(LogLevel.Warn, $"Tablet mode: {ex.Message}");
        }
        await SendStateAsync(error, scale);
    }

    public Task SendStateAsync(string? error = null, int? scale = null)
    {
        var payload = new JsonObject { ["on"] = IsOn, ["windows"] = Environment.OSVersion.Version.Build >= 22000 ? "11" : "10" };
        if (scale is not null) payload["scale"] = scale;
        if (error is not null) payload["error"] = error;
        return connection.SendToPhoneAsync(Envelope.Create(MessageType.PcTabletModeState, payload), CancellationToken.None);
    }

    /// <summary>Scale the display up and turn on touch spacing. Returns the new scale in percent.</summary>
    private int TurnOn(string? displayName)
    {
        lock (Gate)
        {
            if (IsOn) RestorePending(log); // a fresh start from the user's own settings
            var source = Display.Find(displayName) ?? throw new LincException("couldn't find the display being mirrored.");
            var (current, max) = Display.GetScale(source);
            int target = TargetScale(current, max);
            if (target <= current) throw new LincException($"this display is already at its largest scale ({current} %).");

            int? compact = Registry.CurrentUser.OpenSubKey(Advanced)?.GetValue("UseCompactMode") as int?;
            var saved = new JsonObject { ["display"] = source.Name, ["scale"] = current };
            if (compact is not null) saved["compact"] = compact;
            Directory.CreateDirectory(Path.GetDirectoryName(RestoreFile)!);
            File.WriteAllText(RestoreFile, saved.ToJsonString());

            Display.SetScale(source, target);
            using (var key = Registry.CurrentUser.CreateSubKey(Advanced))
                key.SetValue("UseCompactMode", 0, RegistryValueKind.DWord);
            Broadcast("ShellState");
            log.Log(LogLevel.Info, $"Tablet mode on: {source.Name} scaled {current} % → {target} %, touch spacing on");
            return target;
        }
    }

    /// <summary>Put back the scale and spacing Tablet mode changed, if it is on (crash-safe).</summary>
    public static void RestorePending(ILogService log)
    {
        lock (Gate)
        {
            JsonNode? saved;
            try { saved = File.Exists(RestoreFile) ? JsonNode.Parse(File.ReadAllText(RestoreFile)) : null; }
            catch (Exception ex) when (ex is IOException or JsonException) { saved = null; }
            if (saved is null) { TryDelete(); return; }
            try
            {
                if ((string?)saved["display"] is { } name && (int?)saved["scale"] is { } scale && Display.Find(name) is { } source)
                    Display.SetScale(source, scale);
                using (var key = Registry.CurrentUser.CreateSubKey(Advanced))
                {
                    if ((int?)saved["compact"] is { } compact) key.SetValue("UseCompactMode", compact, RegistryValueKind.DWord);
                    else key.DeleteValue("UseCompactMode", throwOnMissingValue: false);
                }
                Broadcast("ShellState");
                log.Log(LogLevel.Info, "Tablet mode off: display scale and Explorer spacing put back");
            }
            catch (Exception ex) { log.Log(LogLevel.Warn, $"Tablet mode: couldn't put everything back: {ex.Message}"); }
            TryDelete();
        }
    }

    private static void TryDelete() { try { File.Delete(RestoreFile); } catch { } }

    private static void Broadcast(string setting)
    {
        IntPtr text = Marshal.StringToHGlobalUni(setting);
        try { SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, text, 0x0002, 3000, out _); }
        finally { Marshal.FreeHGlobal(text); }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);

    /// <summary>
    /// Per-display scale through DisplayConfig — what Settings → Display → Scale itself uses. The
    /// get/set device-info types (-3/-4) are undocumented but stable since Windows 10 1607; scale is
    /// expressed in steps relative to the display's recommended scale.
    /// </summary>
    internal static class Display
    {
        internal sealed record Source(string Name, LUID Adapter, uint Id);

        public static Source? Find(string? gdiName)
        {
            uint paths = 0, modes = 0;
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out paths, out modes) != 0) return null;
            var pathArray = new DISPLAYCONFIG_PATH_INFO[paths];
            var modeArray = new DISPLAYCONFIG_MODE_INFO[modes];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref paths, pathArray, ref modes, modeArray, IntPtr.Zero) != 0) return null;
            var sources = pathArray.Take((int)paths).Select(p =>
            {
                var name = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                name.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                name.header.size = Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                name.header.adapterId = p.sourceInfo.adapterId;
                name.header.id = p.sourceInfo.id;
                return DisplayConfigGetDeviceInfo(ref name) == 0
                    ? new Source(name.viewGdiDeviceName, p.sourceInfo.adapterId, p.sourceInfo.id)
                    : null;
            }).OfType<Source>().ToList();
            return sources.FirstOrDefault(s => gdiName is not null && s.Name.Equals(gdiName, StringComparison.OrdinalIgnoreCase))
                   ?? sources.FirstOrDefault();
        }

        public static (int Current, int Max) GetScale(Source source)
        {
            var get = new DISPLAYCONFIG_SOURCE_DPI_SCALE_GET();
            get.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE;
            get.header.size = Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DPI_SCALE_GET>();
            get.header.adapterId = source.Adapter;
            get.header.id = source.Id;
            if (DisplayConfigGetDeviceInfo(ref get) != 0) throw new LincException("Windows didn't report this display's scale.");
            int recommended = Math.Abs(get.minScaleRel);
            int Clamp(int i) => Scales[Math.Clamp(i, 0, Scales.Length - 1)];
            return (Clamp(recommended + get.curScaleRel), Clamp(recommended + get.maxScaleRel));
        }

        public static void SetScale(Source source, int percent)
        {
            var get = new DISPLAYCONFIG_SOURCE_DPI_SCALE_GET();
            get.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE;
            get.header.size = Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DPI_SCALE_GET>();
            get.header.adapterId = source.Adapter;
            get.header.id = source.Id;
            if (DisplayConfigGetDeviceInfo(ref get) != 0) throw new LincException("Windows didn't report this display's scale.");
            int index = Array.IndexOf(Scales, percent);
            if (index < 0) throw new LincException($"{percent} % isn't a Windows scale.");
            var set = new DISPLAYCONFIG_SOURCE_DPI_SCALE_SET();
            set.header.type = DISPLAYCONFIG_DEVICE_INFO_SET_DPI_SCALE;
            set.header.size = Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DPI_SCALE_SET>();
            set.header.adapterId = source.Adapter;
            set.header.id = source.Id;
            set.scaleRel = index - Math.Abs(get.minScaleRel);
            if (DisplayConfigSetDeviceInfo(ref set) != 0) throw new LincException($"Windows refused to set the scale to {percent} %.");
        }

        private const uint QDC_ONLY_ACTIVE_PATHS = 2;
        private const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
        private const int DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE = -3;
        private const int DISPLAYCONFIG_DEVICE_INFO_SET_DPI_SCALE = -4;

        [StructLayout(LayoutKind.Sequential)] internal struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public int type; public int size; public LUID adapterId; public uint id; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_SOURCE_DPI_SCALE_GET
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public int minScaleRel, curScaleRel, maxScaleRel;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_SOURCE_DPI_SCALE_SET { public DISPLAYCONFIG_DEVICE_INFO_HEADER header; public int scaleRel; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId; public uint id; public uint modeInfoIdx; public int outputTechnology; public int rotation;
            public int scaling; public uint refreshNumerator; public uint refreshDenominator; public int scanLineOrdering;
            public int targetAvailable; public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO { public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; public uint flags; }

        [StructLayout(LayoutKind.Sequential, Size = 64)]
        private struct DISPLAYCONFIG_MODE_INFO { public int infoType; public uint id; public LUID adapterId; }

        [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
            ref uint modes, [Out] DISPLAYCONFIG_MODE_INFO[] modeArray, IntPtr topologyId);

        [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME request);
        [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DPI_SCALE_GET request);
        [DllImport("user32.dll")] private static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DPI_SCALE_SET request);
    }
}
