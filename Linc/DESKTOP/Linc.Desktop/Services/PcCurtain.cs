using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Linc.Desktop.Services;

/// <summary>
/// The PC's screen, dark while the phone uses it (v28): PC Remote's "PC screen off". A black
/// window covers every monitor, so nobody at the desk sees what is done from the phone, and the
/// stream carries on, because the window asks Windows to leave it out of every capture
/// (<c>WDA_EXCLUDEFROMCAPTURE</c>, Windows 10 2004 and later): Linc's own capture sees the desktop
/// under it. The monitors themselves stay on — turning them off really would stop the picture.
///
/// <para>Clicks and keys go straight through it (it is transparent to input and never takes
/// focus), except for one button in each monitor's corner: "✕ Turn the screen back on", for
/// whoever is at the PC. It lifts by itself when the phone goes away too. A PC that can't exclude a window from
/// capture never shows it: a black screen on the phone too would be worse than none.</para>
/// </summary>
public sealed class PcCurtain(Func<bool> stillWanted, ILogService log, Action? liftedAtPc = null)
{
    private static volatile bool _userLifted;
    private static readonly List<IntPtr> Buttons = [];
    private readonly object _lock = new();
    private Thread? _thread;
    private uint _threadId;
    private volatile bool _on;
    private string? _error;

    public bool On => _on;

    /// <summary>Windows 10 2004 (build 19041) is the first that can leave a window out of captures.</summary>
    public static bool Available => Environment.OSVersion.Version.Build >= 19041;

    public JsonObject State()
    {
        var o = new JsonObject { ["on"] = _on, ["available"] = Available };
        if (_error is not null) o["error"] = _error;
        return o;
    }

    public void Set(bool on)
    {
        lock (_lock)
        {
            if (on == _on) return;
            if (on)
            {
                if (!Available) { _error = "This version of Windows can't hide the PC's screen without hiding it from the phone too (Windows 10 2004 or later is needed)."; return; }
                _error = null;
                var ready = new ManualResetEventSlim();
                _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "linc-curtain" };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
                ready.Wait(3000);
            }
            else
            {
                _on = false;
                if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                _thread?.Join(2000);
                _thread = null;
                _threadId = 0;
                log.Log(LogLevel.Info, "PC tools: the PC's screen is back");
            }
        }
    }

    private void Run(ManualResetEventSlim ready)
    {
        var windows = new List<IntPtr>();
        _threadId = GetCurrentThreadId();
        try
        {
            var wndProc = new WndProc(Proc);
            var cls = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
                hInstance = GetModuleHandle(null),
                hbrBackground = GetStockObject(BLACK_BRUSH),
                lpszClassName = "LincCurtain",
                hCursor = IntPtr.Zero,
            };
            RegisterClassEx(ref cls); // fails harmlessly when already registered by an earlier curtain

            var monitors = new List<RECT>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr dc, ref RECT r, IntPtr data) => { monitors.Add(r); return true; }, IntPtr.Zero);
            foreach (var r in monitors)
            {
                var hwnd = CreateWindowEx(
                    WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE,
                    "LincCurtain", "Linc", WS_POPUP, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
                    IntPtr.Zero, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
                if (hwnd == IntPtr.Zero) continue;
                // Leave it out of every capture first; only then may it be shown.
                if (!SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE))
                {
                    DestroyWindow(hwnd);
                    _error = "Windows wouldn't keep the dark screen out of the phone's picture, so it stays off.";
                    log.Log(LogLevel.Warn, $"PC tools: the curtain couldn't be left out of capture (error {Marshal.GetLastWin32Error()})");
                    continue;
                }
                SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
                ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                windows.Add(hwnd);
            }
            // The way back for whoever is at the PC: a small solid button in each monitor's
            // top-right corner (also left out of the capture, so the phone never sees it).
            _userLifted = false;
            lock (Buttons) Buttons.Clear();
            if (windows.Count > 0)
            {
                foreach (var r in monitors)
                {
                    var button = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "LincCurtain", "Turn the screen back on",
                        WS_POPUP, r.Right - ButtonWidth - 24, r.Top + 24, ButtonWidth, ButtonHeight, IntPtr.Zero, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
                    if (button == IntPtr.Zero) continue;
                    if (!SetWindowDisplayAffinity(button, WDA_EXCLUDEFROMCAPTURE)) { DestroyWindow(button); continue; }
                    lock (Buttons) Buttons.Add(button);
                    ShowWindow(button, SW_SHOWNOACTIVATE);
                    windows.Add(button);
                }
            }
            _on = windows.Count > 0;
            if (_on) log.Log(LogLevel.Info, $"PC tools: the PC's screen is dark while the phone uses it ({windows.Count} monitor{(windows.Count == 1 ? "" : "s")}); the phone still sees it");
            ready.Set();
            if (!_on) return;

            // The phone going away lifts it: nobody would be left to turn it off.
            SetTimer(IntPtr.Zero, IntPtr.Zero, 2000, IntPtr.Zero);
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (_userLifted)
                {
                    log.Log(LogLevel.Info, "PC tools: the screen was turned back on at the PC");
                    break;
                }
                if (msg.message == WM_TIMER && !stillWanted())
                {
                    log.Log(LogLevel.Info, "PC tools: the phone went away; the PC's screen is back");
                    break;
                }
                // Keep it on top of anything that has asked to be topmost since.
                if (msg.message == WM_TIMER)
                    foreach (var w in windows) SetWindowPos(w, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            GC.KeepAlive(wndProc);
        }
        catch (Exception ex)
        {
            _error = "The PC's screen couldn't be darkened.";
            log.Log(LogLevel.Warn, $"PC tools: curtain failed ({ex.Message})");
        }
        finally
        {
            foreach (var w in windows) DestroyWindow(w);
            lock (Buttons) Buttons.Clear();
            _on = false;
            ready.Set();
            if (_userLifted) liftedAtPc?.Invoke();
        }
    }

    /// <summary>
    /// Paints the curtain (black, one quiet line saying why) and the corner button, and turns a
    /// click on the button into "back on".
    /// </summary>
    private static IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        bool isButton;
        lock (Buttons) isButton = Buttons.Contains(hwnd);
        if (msg == WM_LBUTTONUP && isButton)
        {
            _userLifted = true;
            PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero); // wake the loop so it sees it
            return IntPtr.Zero;
        }
        if (msg == WM_SETCURSOR && isButton)
        {
            SetCursor(LoadCursor(IntPtr.Zero, IDC_HAND));
            return new IntPtr(1);
        }
        if (msg == WM_PAINT)
        {
            var dc = BeginPaint(hwnd, out var ps);
            GetClientRect(hwnd, out var rect);
            if (isButton)
            {
                var fill = CreateSolidBrush(0x00303030);
                FillRect(dc, ref rect, fill);
                DeleteObject(fill);
                SetBkMode(dc, TRANSPARENT);
                SetTextColor(dc, 0x00F0F0F0);
                DrawText(dc, "\u2715   Turn the screen back on", -1, ref rect, DT_CENTER | DT_SINGLELINE | DT_VCENTER);
            }
            else
            {
                FillRect(dc, ref rect, GetStockObject(BLACK_BRUSH));
                SetBkMode(dc, TRANSPARENT);
                SetTextColor(dc, 0x00505050);
                rect.Top = rect.Bottom - 80;
                DrawText(dc, "This PC is being used from Linc on a phone", -1, ref rect, DT_CENTER | DT_SINGLELINE | DT_VCENTER);
            }
            EndPaint(hwnd, ref ps);
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private const int ButtonWidth = 300, ButtonHeight = 48;

    // ---- Win32 -----------------------------------------------------------------------------------

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate bool MonitorEnum(IntPtr monitor, IntPtr dc, ref RECT rect, IntPtr data);

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const uint LWA_ALPHA = 0x2;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint WM_QUIT = 0x12, WM_PAINT = 0xF, WM_TIMER = 0x113, WM_NULL = 0, WM_LBUTTONUP = 0x202, WM_SETCURSOR = 0x20;
    private static readonly IntPtr IDC_HAND = new(32649);
    private const int BLACK_BRUSH = 4, TRANSPARENT = 1;
    private const uint DT_CENTER = 0x1, DT_VCENTER = 0x4, DT_SINGLELINE = 0x20;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT { public IntPtr hdc; public bool fErase; public RECT rcPaint; public bool fRestore, fIncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnum callback, IntPtr data);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT rect, IntPtr brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawText(IntPtr dc, string text, int count, ref RECT rect, uint format);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint color);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
