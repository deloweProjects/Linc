using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Linc.Desktop.Services;

/// <summary>
/// Where one app's PC window was, and how big — the four numbers scrcpy takes as
/// <c>--window-x/--window-y/--window-width/--window-height</c>. Screen coordinates, physical
/// pixels, exactly as user32 reports them.
/// </summary>
public readonly record struct AppWindowGeometry(int X, int Y, int Width, int Height)
{
    /// <summary>A rectangle we would never hand to scrcpy: zero/negative size, or absurdly large.</summary>
    public bool IsPlausible => Width is > 120 and <= 16384 && Height is > 120 and <= 16384;
}

/// <summary>
/// Per-device, per-app window geometry (M7b): <c>&lt;root&gt;\cache\&lt;serial&gt;\appwindows.json</c>,
/// beside <c>apps.json</c>, because the same phone's Camera window and another phone's Camera
/// window are different windows.
///
/// <para><b>The root is injected, never resolved here</b> — it comes from
/// <see cref="DeviceRegistry.RootPath"/>, exactly as <see cref="AppCatalog"/> takes it
/// (D-057/D-058). A <see cref="Environment.GetFolderPath"/> call in this file would quietly
/// reopen the hole those two decisions closed, which is why <c>applaunchsim</c> greps for it.</para>
///
/// <para>Nothing in here throws for a bad file. A corrupt or unreadable <c>appwindows.json</c>
/// degrades to "no memory" — scrcpy then places the window itself, which is the same outcome as a
/// first launch and strictly better than a crash.</para>
/// </summary>
public sealed class AppWindowStore
{
    private const string FileName = "appwindows.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _root;
    private readonly ILogService? _log;
    private readonly object _lock = new();

    /// <param name="rootPath">The Linc store directory — pass <see cref="DeviceRegistry.RootPath"/>.</param>
    public AppWindowStore(string rootPath, ILogService? log = null)
    {
        _root = rootPath;
        _log = log;
    }

    /// <summary>Convenience overload: take the root from the registry, which is the rule (D-058).</summary>
    public AppWindowStore(DeviceRegistry registry, ILogService? log = null)
        : this(registry.RootPath, log)
    {
    }

    /// <summary>The appwindows.json path for one phone. Shares <see cref="AppCatalog"/>'s directory.</summary>
    public string PathFor(string serial) =>
        Path.Combine(_root, "cache", Sanitize(serial), FileName);

    /// <summary>
    /// Every remembered rectangle for one phone, keyed by package. Empty when there is no file,
    /// it cannot be read, or its contents are not a well-formed map — never throws.
    /// </summary>
    public IReadOnlyDictionary<string, AppWindowGeometry> Load(string serial)
    {
        var path = PathFor(serial);
        try
        {
            if (!File.Exists(path))
            {
                return new Dictionary<string, AppWindowGeometry>(StringComparer.Ordinal);
            }
            var stored = JsonSerializer.Deserialize<Dictionary<string, StoredGeometry>>(File.ReadAllText(path));
            if (stored is null)
            {
                return new Dictionary<string, AppWindowGeometry>(StringComparer.Ordinal);
            }
            var result = new Dictionary<string, AppWindowGeometry>(StringComparer.Ordinal);
            foreach (var (package, value) in stored)
            {
                if (string.IsNullOrWhiteSpace(package) || value is null)
                {
                    continue;
                }
                var geometry = new AppWindowGeometry(value.X, value.Y, value.Width, value.Height);
                // A stored rectangle we would never hand to scrcpy is dropped on read, so a
                // hand-edited or truncated file cannot produce a 3-pixel window.
                if (geometry.IsPlausible)
                {
                    result[package] = geometry;
                }
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or NotSupportedException)
        {
            _log?.Log(LogLevel.Warn,
                $"Apps: remembered window positions for {serial} were unreadable ({ex.GetType().Name}); starting fresh.");
            return new Dictionary<string, AppWindowGeometry>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// The remembered rectangle for one app, or null when there is none. Callers must still run it
    /// past <see cref="IsOnScreen"/> before using it — a rectangle can be perfectly well-formed and
    /// still land on a monitor that is no longer plugged in.
    /// </summary>
    public AppWindowGeometry? Get(string serial, string package) =>
        Load(serial).TryGetValue(package, out var geometry) ? geometry : null;

    /// <summary>
    /// Remembers one app's rectangle. Returns false (and logs) rather than throwing: forgetting
    /// where a window was is not worth taking the app down for. Implausible rectangles are refused
    /// — storing a wrong one is worse than storing nothing.
    /// </summary>
    public bool Save(string serial, string package, AppWindowGeometry geometry)
    {
        if (string.IsNullOrWhiteSpace(package) || !geometry.IsPlausible)
        {
            return false;
        }
        lock (_lock)
        {
            var path = PathFor(serial);
            try
            {
                var all = Load(serial).ToDictionary(
                    kv => kv.Key,
                    kv => new StoredGeometry
                    {
                        X = kv.Value.X, Y = kv.Value.Y, Width = kv.Value.Width, Height = kv.Value.Height,
                    },
                    StringComparer.Ordinal);
                all[package] = new StoredGeometry
                {
                    X = geometry.X, Y = geometry.Y, Width = geometry.Width, Height = geometry.Height,
                };
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(all, JsonOptions));
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.Log(LogLevel.Warn,
                    $"Apps: could not remember the window position for {package}: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// The on-screen rule, kept pure so it can be checked without a display attached.
    ///
    /// <para>A remembered rectangle is used only when <b>its top edge is reachable</b>: at least
    /// <see cref="MinVisibleWidth"/>×<see cref="MinVisibleHeight"/> of its <i>title-bar strip</i>
    /// (the top <see cref="TitleStripHeight"/> pixels) overlaps some monitor's visible area. That
    /// is deliberately stricter than "overlaps a monitor somewhere": a window whose only visible
    /// corner is its bottom-right cannot be dragged back, so restoring it is no kinder than not
    /// restoring it. Anything that fails is discarded and scrcpy places the window itself.</para>
    /// </summary>
    public static bool IsOnScreen(
        AppWindowGeometry geometry, IReadOnlyList<(int X, int Y, int Width, int Height)> monitors)
    {
        if (!geometry.IsPlausible || monitors.Count == 0)
        {
            return false;
        }
        var stripHeight = Math.Min(TitleStripHeight, geometry.Height);
        foreach (var (mx, my, mw, mh) in monitors)
        {
            var left = Math.Max(geometry.X, mx);
            var top = Math.Max(geometry.Y, my);
            var right = Math.Min(geometry.X + geometry.Width, mx + mw);
            var bottom = Math.Min(geometry.Y + stripHeight, my + mh);
            if (right - left >= MinVisibleWidth && bottom - top >= MinVisibleHeight)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The strip that has to be reachable — a window's grab handle, in effect.</summary>
    public const int TitleStripHeight = 40;

    public const int MinVisibleWidth = 120;
    public const int MinVisibleHeight = 20;

    /// <summary>
    /// The monitors' visible areas, in the same virtual-screen coordinates a window rectangle uses.
    /// Separated from <see cref="IsOnScreen"/> so the rule is testable and only this half needs a
    /// real desktop.
    /// </summary>
    public static IReadOnlyList<(int X, int Y, int Width, int Height)> CurrentMonitors()
    {
        var monitors = new List<(int, int, int, int)>();
        try
        {
            bool Callback(IntPtr monitor, IntPtr _, ref Rect __, IntPtr ___)
            {
                var info = new MonitorInfo { CbSize = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    // Work area, not the full monitor: a window remembered under the taskbar is
                    // one the owner cannot grab either.
                    monitors.Add((
                        info.WorkArea.Left,
                        info.WorkArea.Top,
                        info.WorkArea.Right - info.WorkArea.Left,
                        info.WorkArea.Bottom - info.WorkArea.Top));
                }
                return true;
            }
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // No desktop to ask (a harness, a service session): "no monitors" makes IsOnScreen
            // discard everything, which is the safe direction.
        }
        return monitors;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int CbSize;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    /// <summary>
    /// Serials become path segments and are not ours to trust. Deliberately <see cref="AppCatalog"/>'s
    /// own routine rather than a copy: the two stores share a directory, so two sanitisers that
    /// drifted apart would put one phone's files in two places.
    /// </summary>
    private static string Sanitize(string value) => AppCatalog.Sanitize(value);

    /// <summary>On-disk shape. Its own type: the file format is ours, not the wire's.</summary>
    private sealed class StoredGeometry
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }
}
