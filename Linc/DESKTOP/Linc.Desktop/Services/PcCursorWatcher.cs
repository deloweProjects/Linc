using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;

namespace Linc.Desktop.Services;

/// <summary>
/// One cursor sample for the phone: where the pointer is on the mirrored display and, when it
/// changed, what it looks like (v20 <c>pc.mirror.cursor</c>, D-067).
/// </summary>
public sealed record CursorSample(int X, int Y, bool Visible, long ShapeId, CursorShape? Shape);

/// <summary>A cursor image as straight (non-premultiplied) ARGB, row-major, top-down.</summary>
public sealed record CursorShape(int Width, int Height, int HotX, int HotY, int[] Argb);

/// <summary>
/// Watches the PC's mouse pointer so the phone can draw it itself (v20, D-067).
///
/// <para><b>Why the phone draws the cursor.</b> Desktop Duplication never puts the pointer in
/// the captured image — that is why the mirror showed no cursor at all — and baking it into
/// the video would tie the most latency-sensitive thing on screen to the slowest path there
/// is: capture, encode, a congested Wi-Fi queue, decode. Remote Desktop solved this decades
/// ago by sending the pointer separately and letting the client render it; that is the main
/// reason an RDP session <i>feels</i> instant on a link where its video is visibly behind. Here
/// the position travels on the control channel (tiny, never queued behind video), and the
/// shape is sent once per distinct cursor and cached by id on the phone.</para>
///
/// <para>Sampled with <c>GetCursorInfo</c> rather than the duplication's pointer metadata:
/// duplication only reports pointer updates alongside a frame, and the feeder deliberately
/// skips frames when the link is busy (D-066) — exactly when an independent cursor matters.
/// The process is PerMonitorV2, so positions are physical pixels, the same space as the
/// capture and as <see cref="PcDisplay"/>.</para>
/// </summary>
public static class PcCursorWatcher
{
    /// <summary>~60 Hz. A sample only goes out when something changed, so idle costs nothing.</summary>
    private const int IntervalMs = 15;

    /// <summary>
    /// Run until <paramref name="token"/> is cancelled, calling <paramref name="send"/> for
    /// each change. <paramref name="send"/> may block (a slow link); samples taken meanwhile
    /// coalesce, because the next one is simply read fresh.
    /// </summary>
    public static void Run(PcDisplay display, Action<CursorSample> send, CancellationToken token)
    {
        var sentShapes = new HashSet<long>();
        int lastX = int.MinValue, lastY = int.MinValue;
        bool lastVisible = false;
        IntPtr lastHandle = IntPtr.Zero;

        while (!token.IsCancellationRequested)
        {
            var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (GetCursorInfo(ref info))
            {
                bool visible = (info.flags & CURSOR_SHOWING) != 0 && info.hCursor != IntPtr.Zero;
                int x = info.ptScreenPos.X - display.Left;
                int y = info.ptScreenPos.Y - display.Top;
                bool shapeChanged = visible && info.hCursor != lastHandle;

                if (x != lastX || y != lastY || visible != lastVisible || shapeChanged)
                {
                    long id = info.hCursor.ToInt64();
                    CursorShape? shape = null;
                    if (shapeChanged && !sentShapes.Contains(id))
                    {
                        shape = Capture(info.hCursor);
                        if (shape is not null) sentShapes.Add(id);
                    }

                    try { send(new CursorSample(x, y, visible, id, shape)); }
                    catch { if (shape is not null) sentShapes.Remove(id); /* resend next time */ }

                    lastX = x; lastY = y; lastVisible = visible;
                    if (visible) lastHandle = info.hCursor;
                }
            }
            token.WaitHandle.WaitOne(IntervalMs);
        }
    }

    /// <summary>The wire form of a sample (payload of <c>pc.mirror.cursor</c>).</summary>
    public static JsonObject ToPayload(CursorSample sample)
    {
        var payload = new JsonObject
        {
            ["x"] = sample.X,
            ["y"] = sample.Y,
            ["visible"] = sample.Visible,
            ["id"] = sample.ShapeId,
        };
        if (sample.Shape is { } shape)
        {
            var bytes = new byte[shape.Argb.Length * 4];
            Buffer.BlockCopy(shape.Argb, 0, bytes, 0, bytes.Length); // little-endian: B,G,R,A per pixel
            payload["shape"] = new JsonObject
            {
                ["w"] = shape.Width,
                ["h"] = shape.Height,
                ["hx"] = shape.HotX,
                ["hy"] = shape.HotY,
                ["bgra"] = Convert.ToBase64String(bytes),
            };
        }
        return payload;
    }

    /// <summary>
    /// Render a cursor handle to ARGB. Colour cursors with real alpha are taken as-is; colour
    /// cursors without alpha take it from the AND mask; monochrome cursors (the I-beam among
    /// them) are decoded from their AND/XOR mask pair.
    /// </summary>
    public static CursorShape? Capture(IntPtr cursor)
    {
        if (!GetIconInfo(cursor, out var icon)) return null;
        try
        {
            var mask = ReadBitmap(icon.hbmMask);
            if (mask is null) return null;

            if (icon.hbmColor != IntPtr.Zero)
            {
                var color = ReadBitmap(icon.hbmColor);
                if (color is null) return null;
                var (w, h, px) = color.Value;
                bool hasAlpha = false;
                foreach (int p in px) if ((p & unchecked((int)0xFF000000)) != 0) { hasAlpha = true; break; }
                if (!hasAlpha)
                {
                    var (_, _, m) = mask.Value;
                    for (int i = 0; i < px.Length; i++)
                        px[i] = (m[i] & 0x00FFFFFF) != 0 ? 0 : px[i] | unchecked((int)0xFF000000);
                }
                return new CursorShape(w, h, icon.xHotspot, icon.yHotspot, px);
            }

            var (mw, mh, bits) = mask.Value;
            return new CursorShape(mw, mh / 2, icon.xHotspot, icon.yHotspot, DecodeMonochrome(bits, mw, mh / 2));
        }
        finally
        {
            if (icon.hbmMask != IntPtr.Zero) DeleteObject(icon.hbmMask);
            if (icon.hbmColor != IntPtr.Zero) DeleteObject(icon.hbmColor);
        }
    }

    /// <summary>
    /// Decode a monochrome cursor: the top half is the AND mask, the bottom half the XOR mask.
    /// AND=1/XOR=1 means "invert the screen", which a client-drawn cursor cannot do; it is drawn
    /// white with a black outline so the I-beam stays visible on both light and dark content.
    /// Pure, so it is unit-testable.
    /// </summary>
    public static int[] DecodeMonochrome(int[] maskPixels, int width, int height)
    {
        const int Black = unchecked((int)0xFF000000), White = unchecked((int)0xFFFFFFFF);
        var output = new int[width * height];
        var invert = new bool[width * height];
        for (int i = 0; i < output.Length; i++)
        {
            bool and = (maskPixels[i] & 0x00FFFFFF) != 0;
            bool xor = (maskPixels[i + output.Length] & 0x00FFFFFF) != 0;
            output[i] = (and, xor) switch
            {
                (false, false) => Black,
                (false, true) => White,
                (true, false) => 0,
                (true, true) => White,
            };
            invert[i] = and && xor;
        }
        // Outline the inverted pixels.
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                if (output[i] != 0) continue;
                for (int dy = -1; dy <= 1 && output[i] == 0; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                        if (invert[ny * width + nx]) { output[i] = Black; break; }
                    }
            }
        return output;
    }

    /// <summary>Read an HBITMAP as 32-bit top-down pixels.</summary>
    private static (int Width, int Height, int[] Pixels)? ReadBitmap(IntPtr bitmap)
    {
        if (bitmap == IntPtr.Zero) return null;
        if (GetObject(bitmap, Marshal.SizeOf<BITMAP>(), out BITMAP bm) == 0) return null;
        int w = bm.bmWidth, h = bm.bmHeight;
        if (w <= 0 || h <= 0 || w > 256 || h > 512) return null;

        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var pixels = new int[w * h];
        IntPtr dc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, bitmap, 0, (uint)h, pixels, ref header, 0) == 0) return null;
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
        return (w, h, pixels);
    }

    // ---- interop -----------------------------------------------------------

    private const int CURSOR_SHOWING = 0x1;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr obj, int size, out BITMAP bitmap);
    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines,
        [Out] int[] bits, ref BITMAPINFOHEADER info, uint usage);
}
