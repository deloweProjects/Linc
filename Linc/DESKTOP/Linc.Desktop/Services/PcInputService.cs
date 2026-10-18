using System;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Linc.Desktop.Protocol;

namespace Linc.Desktop.Services;

/// <summary>The bounds of one display within the virtual desktop, in physical pixels.</summary>
public readonly record struct DisplayRect(int Left, int Top, int Width, int Height);

public interface IPcInputService
{
    /// <summary>
    /// Apply one <c>pc.input</c> message to the PC. <paramref name="target"/> is the display
    /// being mirrored; <paramref name="virtual"/> is the whole virtual desktop.
    /// </summary>
    void Apply(JsonObject payload, DisplayRect target, DisplayRect @virtual);
}

/// <summary>
/// Injects the phone's <c>pc.input</c> events onto the PC with <c>SendInput</c> (M05, D-029).
/// This is the half of the reverse mirror that needs no shell UID, so control works over pure
/// Direct TLS.
///
/// <para><b>The coordinate chain, which the M05 spike warned would bite.</b> The phone sends
/// x/y normalised 0..65535 against the <i>video</i> — one display at its physical resolution.
/// <c>SendInput</c> with <c>MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK</c> instead wants
/// 0..65535 across the <i>whole virtual desktop</i>. So <see cref="MapToVirtualDesktop"/>
/// walks video-normalised → physical pixel on the target display → virtual-desktop-normalised.
/// Because the process is PerMonitorV2 (app.manifest) every one of those is physical pixels
/// with no DPI virtualisation in the way — that is the single assumption the whole chain rests
/// on. The mapping is a pure function so it is unit-tested without moving the real cursor.</para>
///
/// <para><b>Two pointer modes.</b> <c>touch</c> is absolute — the cursor jumps to where you
/// touched. <c>trackpad</c> is relative — a drag nudges the cursor by the delta, like a
/// laptop touchpad, which is easier for precise work on a small phone screen.</para>
/// </summary>
public sealed class PcInputService : IPcInputService
{
    public void Apply(JsonObject payload, DisplayRect target, DisplayRect @virtual)
    {
        string kind = (string?)payload["kind"] ?? "";
        switch (kind)
        {
            case "move":
            case "down":
            case "up":
                Pointer(kind, payload, target, @virtual);
                break;
            case "scroll":
                // v20 "precise": dx/dy are raw wheel units (120 = one notch), so a two-finger
                // drag scrolls smoothly instead of in notch-sized jumps.
                if ((bool?)payload["precise"] == true)
                {
                    int dy = (int?)payload["dy"] ?? 0, dx = (int?)payload["dx"] ?? 0;
                    if (dy != 0) SendWheel(MOUSEEVENTF_WHEEL, dy);
                    if (dx != 0) SendWheel(MOUSEEVENTF_HWHEEL, dx);
                }
                else Scroll((int?)payload["dy"] ?? 0, (int?)payload["dx"] ?? 0);
                break;
            case "key":
                Key((int?)payload["keyCode"] ?? 0, down: (bool?)payload["down"] ?? true);
                break;
            case "text":
                Text((string?)payload["text"] ?? "");
                break;
        }
    }

    private void Pointer(string kind, JsonObject payload, DisplayRect target, DisplayRect @virtual)
    {
        string mode = (string?)payload["mode"] ?? "touch";
        uint flags;

        if (mode == "trackpad")
        {
            // Relative: dx/dy are pixel deltas on the target display, applied as-is.
            flags = MOUSEEVENTF_MOVE;
            SendMouse((int?)payload["dx"] ?? 0, (int?)payload["dy"] ?? 0, flags, absolute: false);
        }
        else if (payload["x"] is not null && payload["y"] is not null)
        {
            var (ax, ay) = MapToVirtualDesktop(
                (int)payload["x"]!, (int)payload["y"]!, target, @virtual);
            SendMouse(ax, ay, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                absolute: true);
        }

        // A down/up carries a click at the same coordinates — left unless the phone says
        // otherwise (v20 "button": press-and-hold / two-finger tap is a right click).
        if (kind is "down" or "up")
            SendMouse(0, 0, ButtonFlag((string?)payload["button"], down: kind == "down"), absolute: false);
    }

    /// <summary>
    /// The display a <c>pc.input</c> lands on: the mirrored one while Mirror PC runs; otherwise
    /// the primary display (the phone's Remote trackpad, keyboard and presentation keys work with
    /// no mirror at all); with no display list, the whole virtual desktop. Pure.
    /// </summary>
    public static DisplayRect TargetFor(PcDisplay? mirrored, IReadOnlyList<PcDisplay> displays, DisplayRect virtualDesktop)
    {
        var display = mirrored ?? displays.FirstOrDefault(d => d.Primary) ?? displays.FirstOrDefault();
        return display is null
            ? virtualDesktop
            : new DisplayRect(display.Left, display.Top, display.Width, display.Height);
    }

    /// <summary>The SendInput flag for a button press or release. Unknown names are a left click.</summary>
    public static uint ButtonFlag(string? button, bool down) => button switch
    {
        "right" => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
        "middle" => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
        _ => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
    };

    /// <summary>
    /// Keys that live on the extended (E0-prefixed) part of the keyboard. Sent without the flag
    /// they arrive as their numpad twins — an arrow becomes numpad 4 to apps that read scan
    /// codes, and Delete becomes numpad-decimal — so the phone's extra-keys row needs it.
    /// </summary>
    public static bool IsExtendedKey(int virtualKey) => virtualKey is
        0x21 or 0x22 or 0x23 or 0x24 or          // PgUp, PgDn, End, Home
        0x25 or 0x26 or 0x27 or 0x28 or          // arrows
        0x2D or 0x2E or                          // Insert, Delete
        0x5B or 0x5C or 0x5D or                  // Win keys, Apps (context menu)
        0xA3 or 0xA5 or                          // right Ctrl, right Alt
        0x6F or 0x90 or 0x2C;                    // numpad divide, Num Lock, Print Screen

    /// <summary>
    /// Map a video-normalised point (0..65535 against the mirrored display) to the
    /// 0..65535 virtual-desktop coordinates <c>MOUSEEVENTF_VIRTUALDESK</c> expects. Pure.
    /// </summary>
    public static (int X, int Y) MapToVirtualDesktop(int videoX, int videoY,
        DisplayRect target, DisplayRect @virtual)
    {
        // video-normalised -> physical pixel on the target display
        long px = target.Left + (long)videoX * (target.Width - 1) / 65535;
        long py = target.Top + (long)videoY * (target.Height - 1) / 65535;

        // physical pixel -> virtual-desktop-normalised (n = v*65535/(size-1) maps both ends exactly)
        int nx = (int)((px - @virtual.Left) * 65535 / Math.Max(1, @virtual.Width - 1));
        int ny = (int)((py - @virtual.Top) * 65535 / Math.Max(1, @virtual.Height - 1));
        return (Math.Clamp(nx, 0, 65535), Math.Clamp(ny, 0, 65535));
    }

    private void Scroll(int dy, int dx)
    {
        // One notch is WHEEL_DELTA (120). The phone sends notch counts.
        if (dy != 0) SendWheel(MOUSEEVENTF_WHEEL, dy * WheelDelta);
        if (dx != 0) SendWheel(MOUSEEVENTF_HWHEEL, dx * WheelDelta);
    }

    private void Key(int virtualKey, bool down)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)virtualKey,
                    wScan = (ushort)MapVirtualKey((uint)virtualKey, 0 /* MAPVK_VK_TO_VSC */),
                    dwFlags = (down ? 0 : KEYEVENTF_KEYUP) | (IsExtendedKey(virtualKey) ? KEYEVENTF_EXTENDEDKEY : 0),
                },
            },
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    /// <summary>Type a string as Unicode, independent of keyboard layout.</summary>
    private void Text(string text)
    {
        foreach (char c in text)
        {
            var down = UnicodeKey(c, up: false);
            var up = UnicodeKey(c, up: true);
            SendInput(2, [down, up], Marshal.SizeOf<INPUT>());
        }
    }

    private static INPUT UnicodeKey(char c, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wScan = c,
                dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0),
            },
        },
    };

    private static void SendMouse(int dx, int dy, uint flags, bool absolute)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags } },
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private static void SendWheel(uint flags, int amount)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion { mi = new MOUSEINPUT { mouseData = (uint)amount, dwFlags = flags } },
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    // ---- SendInput interop -------------------------------------------------

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004;

    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    private const int WheelDelta = 120;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
