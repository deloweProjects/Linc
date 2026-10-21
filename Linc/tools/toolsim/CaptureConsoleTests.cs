using System.IO.Compression;
using System.Text;
using Linc.Desktop.Services.Tools;

namespace Linc.ToolSim;

internal static class CaptureConsoleTests
{
    public static void Run(Action<bool, string> check)
    {
        // ---- Capture ----
        check(CaptureCommands.Screenrecord(0) == "screenrecord --size 1280x720 --bit-rate 8000000 --time-limit 180 /sdcard/linc-rec.mp4",
            "720p recording is the spec's command");
        check(CaptureCommands.Screenrecord(1).Contains("--size 1920x1080 --bit-rate 16000000"), "1080p recording");
        check(!CaptureCommands.Screenrecord(2).Contains("--size"), "Max quality records at the phone's own size");
        check(CaptureCommands.DemoEnter.Count == 6 && CaptureCommands.DemoEnter[0] == "settings put global sysui_demo_allowed 1"
              && CaptureCommands.DemoEnter[2].EndsWith("-e command clock -e hhmm 1200"), "clean status bar follows TOOLS.md §4");
        check(CaptureCommands.DemoExit.EndsWith("-e command exit"), "clean status bar exits");
        check(CaptureCommands.FileName(new DateTime(2026, 10, 21, 14, 5, 9), "png") == "phone-20261021-140509.png", "capture file names sort by time");
        check(CaptureCommands.Clock(0) == "00:00" && CaptureCommands.Clock(75) == "01:15" && CaptureCommands.Clock(180) == "03:00", "record clock is mm:ss");

        check(ScreenshotCheck.LooksBlack(Png(64, 48, 4, (x, y) => (0, 0, 0))), "an all-black RGBA screenshot reads as blocked");
        check(ScreenshotCheck.LooksBlack(Png(64, 48, 3, (x, y) => (3, 2, 5), filter: 4)), "near-black RGB (Paeth filtered) reads as blocked");
        check(!ScreenshotCheck.LooksBlack(Png(64, 48, 4, (x, y) => x < 8 && y < 8 ? (255, 255, 255) : (0, 0, 0), filter: 1)),
            "a dark screen with some text is not blocked");
        check(!ScreenshotCheck.LooksBlack(Png(64, 48, 4, (x, y) => (240, 240, 240), filter: 2)), "a white screen is not blocked");
        check(!ScreenshotCheck.LooksBlack(Png(64, 48, 4, (x, y) => (x * 4, y * 5, 90), filter: 3)), "a colourful screen is not blocked");
        check(!ScreenshotCheck.LooksBlack([1, 2, 3]) && !ScreenshotCheck.LooksBlack(Encoding.ASCII.GetBytes("not a png at all, just text here")),
            "garbage is never called blocked");
        var truncated = Png(64, 48, 4, (x, y) => (0, 0, 0));
        check(!ScreenshotCheck.LooksBlack(truncated[..(truncated.Length / 2)]), "a cut-off PNG is never called blocked");

        // ---- Console ----
        check(ConsoleRisk.Describe("rm -rf /sdcard/x") == "deletes files", "rm is risky");
        check(ConsoleRisk.Describe("pm clear com.app") == "wipes an app's data", "pm clear is risky");
        check(ConsoleRisk.Describe("settings put system font_scale 1.3") is not null, "settings put is risky");
        check(ConsoleRisk.Describe("settings get system font_scale") is null, "settings get is not");
        check(ConsoleRisk.Describe("ls -la; rm x") == "deletes files", "a risky verb after ; counts");
        check(ConsoleRisk.Describe("ls | wm size 100x100") is not null, "a risky verb after | counts");
        check(ConsoleRisk.Describe("pm list packages") is null && ConsoleRisk.Describe("rmdir") is null
              && ConsoleRisk.Describe("getprop ro.product.model") is null && ConsoleRisk.Describe("") is null, "everyday commands are not risky");
        check(ConsoleRisk.Describe("  reboot") == "restarts the phone", "leading spaces don't hide a verb");
        check(ConsoleRisk.Describe("su rm x") == "deletes files", "su in front still counts");

        var h = new CommandHistory();
        check(h.Previous("") is null && h.Next() is null, "empty history does nothing");
        h.Add("ls"); h.Add("ls"); h.Add("getprop"); h.Add("df");
        check(h.Items.Count == 3, "a repeat in a row is kept once");
        check(h.Previous("dra") == "df" && h.Previous("") == "getprop" && h.Previous("") == "ls" && h.Previous("") is null, "↑ walks back to the oldest");
        check(h.Next() == "getprop" && h.Next() == "df" && h.Next() == "dra" && h.Next() is null, "↓ walks forward and returns the draft");
        var small = new CommandHistory(max: 2);
        small.Add("a"); small.Add("b"); small.Add("c");
        check(small.Items.Count == 2 && small.Items[0] == "b", "history keeps the newest");

        var buffer = new ConsoleBuffer(maxChars: 20);
        buffer.Append("line one\nline two\n");
        buffer.Append("line three\n");
        check(buffer.Text.Length <= 20 && buffer.Text.EndsWith("line three\n") && !buffer.Text.Contains("one"), "output drops the oldest lines");
        buffer.Clear();
        check(buffer.Text == "", "Clear empties the output");
    }

    /// <summary>A small 8-bit PNG (RGB or RGBA), every row written with one filter type.</summary>
    private static byte[] Png(int w, int h, int channels, Func<int, int, (int R, int G, int B)> pixel, byte filter = 0)
    {
        var stride = w * channels;
        var raw = new byte[(stride + 1) * h];
        var prev = new byte[stride];
        for (var y = 0; y < h; y++)
        {
            var line = new byte[stride];
            for (var x = 0; x < w; x++)
            {
                var (r, g, b) = pixel(x, y);
                line[x * channels] = (byte)r; line[x * channels + 1] = (byte)g; line[x * channels + 2] = (byte)b;
                if (channels == 4) line[x * channels + 3] = 255;
            }
            raw[y * (stride + 1)] = filter;
            for (var i = 0; i < stride; i++)
            {
                int a = i >= channels ? line[i - channels] : 0, up = prev[i], c = i >= channels ? prev[i - channels] : 0;
                var predictor = filter switch
                {
                    1 => a,
                    2 => up,
                    3 => (a + up) / 2,
                    4 => Paeth(a, up, c),
                    _ => 0,
                };
                raw[y * (stride + 1) + 1 + i] = (byte)(line[i] - predictor);
            }
            prev = line;
        }
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        Be(ihdr, 0, w); Be(ihdr, 4, h);
        ihdr[8] = 8; ihdr[9] = (byte)(channels == 4 ? 6 : 2);
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void Be(byte[] buffer, int at, int value)
    {
        buffer[at] = (byte)(value >> 24); buffer[at + 1] = (byte)(value >> 16); buffer[at + 2] = (byte)(value >> 8); buffer[at + 3] = (byte)value;
    }

    // The reader skips CRCs, so these are written as zeros.
    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        Be(len, 0, data.Length);
        s.Write(len);
        s.Write(Encoding.ASCII.GetBytes(type));
        s.Write(data);
        s.Write(new byte[4]);
    }
}
