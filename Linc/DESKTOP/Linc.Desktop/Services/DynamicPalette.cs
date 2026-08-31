namespace Linc.Desktop.Services;

/// <summary>A plain RGB triple. Deliberately not <c>Windows.UI.Color</c> so this file carries no
/// WinUI dependency and a harness can link it and prove the contrast rule (GUIDE 4.1).</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb FromHex(string hex)
    {
        var span = hex.AsSpan(hex.StartsWith('#') ? 1 : 0);
        var value = uint.Parse(span, System.Globalization.NumberStyles.HexNumber);
        return new Rgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// M18 Part B — derives the M3 tonal roles from the phone's wallpaper colour, **with a contrast
/// floor that is guaranteed by construction rather than hoped for**.
///
/// <para>Pure statics, no WinUI and no I/O, so <c>tools\palettesim</c> proves the floor by sweeping
/// the whole colour cube instead of eyeballing one wallpaper (B3: "it looked fine on my wallpaper"
/// is not evidence).</para>
///
/// <para><b>THE CLAMPING RULE, in one sentence:</b> tint the neutral surface toward the wallpaper's
/// dominant colour by at most <see cref="MaxTint"/>, then, while the resulting surface fails the
/// required ratio against the theme's body-text colour, walk the surface back toward the theme's
/// own extreme (white in light, black in dark) in <see cref="ClampStep"/> increments until it
/// passes. Because pure white against the dark ink and pure black against the light ink both clear
/// the floor by a wide margin, the loop always terminates on a passing colour — so a dark wallpaper
/// cannot produce grey-on-grey, it just produces a less tinted surface.</para>
/// </summary>
public static class DynamicPalette
{
    /// <summary>WCAG AA for body text.</summary>
    public const double BodyContrast = 4.5;

    /// <summary>WCAG AA for large text, icons and outlines.</summary>
    public const double LargeContrast = 3.0;

    /// <summary>The most of the wallpaper's colour that may ever reach a surface (M15b used 0.14).</summary>
    public const double MaxTint = 0.14;

    /// <summary>How far the clamp walks per iteration when a tinted surface fails the floor.</summary>
    public const double ClampStep = 0.05;

    // ---- M14's neutral black-and-white ramp: the BASE STATE, not a fallback bolted on. ----
    // Everything below is expressed as a tint of these, so "no wallpaper" is simply tint = 0.
    // Transcribed from the SHIPPED ramp, Themes\MaterialExpressive.xaml lines 20-24 and 40-44 —
    // not invented here. ANDROID\...\ui\theme\Color.kt holds the identical values, which is what
    // makes M18 B2 already true: both apps agree when neither has colour to work with.
    public static readonly Rgb LightSurface = Rgb.FromHex("#FFFFFF");
    public static readonly Rgb LightSurfaceContainer = Rgb.FromHex("#F7F7F7");
    public static readonly Rgb LightOnSurface = Rgb.FromHex("#171717");
    public static readonly Rgb LightOnSurfaceVariant = Rgb.FromHex("#525252");

    public static readonly Rgb DarkSurface = Rgb.FromHex("#131313");
    public static readonly Rgb DarkSurfaceContainer = Rgb.FromHex("#1F1F1F");
    public static readonly Rgb DarkOnSurface = Rgb.FromHex("#E6E6E6");
    public static readonly Rgb DarkOnSurfaceVariant = Rgb.FromHex("#C9C9C9");

    /// <summary>The derived roles. Hex strings so the caller can drop them straight into brushes.</summary>
    public readonly record struct Palette(
        Rgb Surface, Rgb SurfaceContainer, Rgb OnSurface, Rgb OnSurfaceVariant);

    /// <summary>
    /// The neutral ramp with no wallpaper influence at all — what the app ships with, what it falls
    /// back to when the phone is disconnected, when no wallpaper is available, and when the user
    /// turns "Use my phone's colours" off. Identical to <see cref="Derive"/> with a null dominant.
    /// </summary>
    public static Palette Neutral(bool isDark) => isDark
        ? new Palette(DarkSurface, DarkSurfaceContainer, DarkOnSurface, DarkOnSurfaceVariant)
        : new Palette(LightSurface, LightSurfaceContainer, LightOnSurface, LightOnSurfaceVariant);

    /// <summary>
    /// Derives the palette for <paramref name="dominant"/>. A null dominant returns
    /// <see cref="Neutral"/> exactly — the fallback IS the base state (B1).
    /// </summary>
    public static Palette Derive(Rgb? dominant, bool isDark)
    {
        var neutral = Neutral(isDark);
        if (dominant is not { } dom)
        {
            return neutral;
        }

        // The theme's own extreme, which the clamp walks back toward.
        var extreme = isDark ? new Rgb(0, 0, 0) : new Rgb(255, 255, 255);

        var surface = ClampToContrast(Blend(neutral.Surface, dom, MaxTint), neutral.OnSurface, extreme, BodyContrast);
        var container = ClampToContrast(Blend(neutral.SurfaceContainer, dom, MaxTint), neutral.OnSurface, extreme, BodyContrast);

        // Body text and the muted caption colour are both held to the BODY floor against the
        // surface they actually sit on — the caption is the one that historically goes grey-on-grey.
        var onSurface = neutral.OnSurface;
        var onVariant = ClampToContrast(neutral.OnSurfaceVariant, surface, isDark ? new Rgb(255, 255, 255) : new Rgb(0, 0, 0), BodyContrast);

        return new Palette(surface, container, onSurface, onVariant);
    }

    /// <summary>
    /// Walks <paramref name="colour"/> toward <paramref name="towards"/> in <see cref="ClampStep"/>
    /// increments until it clears <paramref name="required"/> against <paramref name="against"/>.
    /// Returns <paramref name="towards"/> itself if even that fails, so the result is deterministic.
    /// </summary>
    public static Rgb ClampToContrast(Rgb colour, Rgb against, Rgb towards, double required)
    {
        if (Contrast(colour, against) >= required)
        {
            return colour;
        }

        for (var t = ClampStep; t < 1.0; t += ClampStep)
        {
            var stepped = Blend(colour, towards, t);
            if (Contrast(stepped, against) >= required)
            {
                return stepped;
            }
        }
        return towards;
    }

    /// <summary>WCAG 2.1 contrast ratio, 1.0 (identical) to 21.0 (black on white).</summary>
    public static double Contrast(Rgb a, Rgb b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var (hi, lo) = la > lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>WCAG relative luminance, with the sRGB gamma expansion (not a naive average).</summary>
    public static double RelativeLuminance(Rgb c) =>
        (0.2126 * Linearise(c.R)) + (0.7152 * Linearise(c.G)) + (0.0722 * Linearise(c.B));

    private static double Linearise(byte channel)
    {
        var v = channel / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    /// <summary>Linear blend: <paramref name="t"/> of <paramref name="b"/> over <paramref name="a"/>.</summary>
    public static Rgb Blend(Rgb a, Rgb b, double t) => new(
        (byte)Math.Clamp(a.R + ((b.R - a.R) * t), 0, 255),
        (byte)Math.Clamp(a.G + ((b.G - a.G) * t), 0, 255),
        (byte)Math.Clamp(a.B + ((b.B - a.B) * t), 0, 255));
}
