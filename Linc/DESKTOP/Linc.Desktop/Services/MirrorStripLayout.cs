namespace Linc.Desktop.Services;

/// <summary>A screen rectangle in physical pixels (left, top, width, height).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>
/// The arithmetic behind the phone buttons beside the mirror (<c>Views/MirrorSideStrip.cs</c>):
/// where the strip sits, how the mirror window is fitted to the phone picture, where scrcpy's
/// bars are, and the swipes. Dependency-free, all in physical pixels.
/// </summary>
public static class MirrorStripLayout
{
    /// <summary>Where the strip goes beside <paramref name="window"/>: flush on its right (or left)
    /// edge, centred on its height. A side with no room on the monitor flips to the other one; with
    /// no room either side (full screen) it sits just inside the window's edge.</summary>
    public static PixelRect Dock(PixelRect window, int stripW, int stripH, bool left, PixelRect monitor)
    {
        bool RoomRight() => window.Right + stripW <= monitor.Right;
        bool RoomLeft() => window.X - stripW >= monitor.X;
        int x;
        if (left ? RoomLeft() : !RoomRight() && RoomLeft())
        {
            x = window.X - stripW;
        }
        else if (RoomRight())
        {
            x = window.Right;
        }
        else
        {
            x = left ? Math.Max(window.X, monitor.X) : Math.Min(window.Right, monitor.Right) - stripW;
        }
        var y = window.Y + (window.Height - stripH) / 2;
        y = Math.Clamp(y, monitor.Y, Math.Max(monitor.Y, monitor.Bottom - stripH));
        return new PixelRect(x, y, stripW, stripH);
    }

    /// <summary>Where scrcpy draws the picture inside <paramref name="client"/>: scaled to fit and
    /// centred, the way scrcpy letterboxes it.</summary>
    public static PixelRect Picture(PixelRect client, int pictureW, int pictureH)
    {
        if (pictureW <= 0 || pictureH <= 0 || client.Width <= 0 || client.Height <= 0)
        {
            return client;
        }
        if ((long)client.Width * pictureH > (long)client.Height * pictureW)
        {
            var w = (int)((long)client.Height * pictureW / pictureH);
            return new PixelRect(client.X + (client.Width - w) / 2, client.Y, w, client.Height);
        }
        var h = (int)((long)client.Width * pictureH / pictureW);
        return new PixelRect(client.X, client.Y + (client.Height - h) / 2, client.Width, h);
    }

    /// <summary>The window's client area shrunk to the picture's shape, keeping its centre, so
    /// there's nothing beside the picture to fill. Null when it already fits (2 px either way).</summary>
    public static PixelRect? Fit(PixelRect client, int pictureW, int pictureH)
    {
        var picture = Picture(client, pictureW, pictureH);
        if (client.Width - picture.Width <= 2 && client.Height - picture.Height <= 2)
        {
            return null;
        }
        return picture;
    }

    /// <summary>The bars scrcpy paints beside (or above and below) the picture, each pulled 1 px
    /// back from it so a cover never hides a row of the picture. Empty when there are none.</summary>
    public static IReadOnlyList<PixelRect> Bars(PixelRect client, int pictureW, int pictureH)
    {
        var picture = Picture(client, pictureW, pictureH);
        var bars = new List<PixelRect>();
        if (picture.X - client.X > 2)
        {
            bars.Add(new PixelRect(client.X, client.Y, picture.X - client.X - 1, client.Height));
            bars.Add(new PixelRect(picture.Right + 1, client.Y, client.Right - picture.Right - 1, client.Height));
        }
        else if (picture.Y - client.Y > 2)
        {
            bars.Add(new PixelRect(client.X, client.Y, client.Width, picture.Y - client.Y - 1));
            bars.Add(new PixelRect(client.X, picture.Bottom + 1, client.Width, client.Bottom - picture.Bottom - 1));
        }
        return bars;
    }

    public enum Swipe { Up, Down, Left, Right }

    /// <summary>The phone's screen in its current orientation: <c>wm size</c> gives it upright, the
    /// picture's shape says whether it's turned.</summary>
    public static (int Width, int Height) Oriented(int phoneW, int phoneH, int pictureW, int pictureH)
    {
        var landscape = pictureW > pictureH;
        return landscape == phoneW > phoneH ? (phoneW, phoneH) : (phoneH, phoneW);
    }

    /// <summary><c>input swipe</c> across the middle of a <paramref name="w"/>x<paramref name="h"/>
    /// screen: up means the finger goes up (the page scrolls on). Kept off the edges, where a swipe
    /// would be the phone's own Back or Home gesture.</summary>
    public static string SwipeCommand(Swipe swipe, int w, int h, int durationMs = 250)
    {
        var (cx, cy) = (w / 2, h / 2);
        var (x1, y1, x2, y2) = swipe switch
        {
            Swipe.Up => (cx, h * 7 / 10, cx, h * 3 / 10),
            Swipe.Down => (cx, h * 3 / 10, cx, h * 7 / 10),
            Swipe.Left => (w * 4 / 5, cy, w / 5, cy),
            _ => (w / 5, cy, w * 4 / 5, cy),
        };
        return $"input swipe {x1} {y1} {x2} {y2} {durationMs}";
    }

    /// <summary>Reads scrcpy's "Texture: 1080x2400" line (printed at the start and on every
    /// rotation). (0, 0) for any other line.</summary>
    public static (int Width, int Height) ParseTexture(string? line)
    {
        if (line is null)
        {
            return (0, 0);
        }
        var at = line.IndexOf("Texture: ", StringComparison.Ordinal);
        if (at < 0)
        {
            return (0, 0);
        }
        var parts = line[(at + 9)..].Trim().Split('x');
        return parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w > 0 && h > 0
            ? (w, h)
            : (0, 0);
    }
}
