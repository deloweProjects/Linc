using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace Linc.Desktop.Services;

public interface IThemeSyncService
{
    /// <summary>Must be called once from the UI thread with the window's root element.</summary>
    void Start(FrameworkElement root);

    /// <summary>Raised on the UI thread after the brush palette was retinted.</summary>
    event Action? PaletteChanged;

    /// <summary>Raised on the UI thread when the phone wallpaper (and its accent) changed.</summary>
    event Action? WallpaperChanged;

    /// <summary>
    /// The phone's wallpaper, or null when unavailable. Sharp since M15b Part E — the phone used
    /// to downsample it to 48 px, and that downsample was the "blur". This side is size- and
    /// format-agnostic and always was, so an older phone build's small PNG still renders here
    /// exactly as before.
    /// </summary>
    BitmapImage? Wallpaper { get; }

    /// <summary>
    /// The same wallpaper decoded tiny (a few dozen pixels), so stretched across the window it
    /// reads as an ambient blur of the phone's wallpaper — the app's own background.
    /// </summary>
    BitmapImage? AmbientWallpaper { get; }

    /// <summary>Current color of a themed brush resource (e.g. "PrimaryContainerBrush"), or null.</summary>
    Windows.UI.Color? BrushColor(string brushKey);
}

/// <summary>
/// Live phone → desktop theme sync (docs/DECISIONS.md D-011): the phone sends its
/// Material You dynamic palette in every status snapshot; this service retints the
/// app's Md* brush resources in place so both apps always look alike. The XAML
/// palette in MaterialExpressive.xaml stays as the disconnected/pre-Android-12
/// default, and SolidColorBrush.Color mutation re-renders everything bound to the
/// brush instances without any resource-dictionary swapping.
/// </summary>
public sealed class ThemeSyncService(IConnectionSupervisor supervisor, IConnectionManager connection, IDeviceRegistry registry, IDeviceCacheService cache) : IThemeSyncService
{
    private static readonly Dictionary<string, string> RoleToBrush = new()
    {
        ["primary"] = "PrimaryBrush",
        ["onPrimary"] = "OnPrimaryBrush",
        ["primaryContainer"] = "PrimaryContainerBrush",
        ["onPrimaryContainer"] = "OnPrimaryContainerBrush",
        ["secondaryContainer"] = "SecondaryContainerBrush",
        ["onSecondaryContainer"] = "OnSecondaryContainerBrush",
        ["tertiaryContainer"] = "TertiaryContainerBrush",
        ["onTertiaryContainer"] = "OnTertiaryContainerBrush",
        ["surface"] = "SurfaceBrush",
        ["onSurface"] = "OnSurfaceBrush",
        ["surfaceContainer"] = "SurfaceContainerBrush",
        ["surfaceContainerHigh"] = "SurfaceContainerHighBrush",
        ["onSurfaceVariant"] = "OnSurfaceVariantBrush",
        ["outline"] = "OutlineBrush",
        ["outlineVariant"] = "OutlineVariantBrush",
        ["error"] = "ErrorBrush",
        ["errorContainer"] = "ErrorContainerBrush",
        ["onErrorContainer"] = "OnErrorContainerBrush",
    };

    /// <summary>
    /// The overlay brushes in MaterialExpressive.xaml (WinUI's own keys, overridden app-wide):
    /// flyouts, menus, ContentDialogs, ComboBox lists, tooltips, teaching tips and the
    /// NavigationView side rail. Frosted glass in the theme's dynamic colour, as on the phone;
    /// retinted here with the surfaceContainer role, density set by MainWindow.ToneSurfaces.
    /// </summary>
    public static readonly string[] OverlayBrushKeys =
    {
        "FlyoutPresenterBackground",
        "MenuFlyoutPresenterBackground",
        "CommandBarFlyoutBackground",
        "ContentDialogBackground",
        "ComboBoxDropDownBackground",
        "AutoSuggestBoxSuggestionsListBackground",
        "ToolTipBackgroundBrush",
        "TeachingTipBackgroundBrush",
        "TeachingTipTransientBackground",
        "NavigationViewExpandedPaneBackground",
        "NavigationViewDefaultPaneBackground",
        "NavigationViewTopPaneBackground",
    };

    public event Action? PaletteChanged;
    public event Action? WallpaperChanged;

    public BitmapImage? Wallpaper { get; private set; }
    public BitmapImage? AmbientWallpaper { get; private set; }

    public Windows.UI.Color? BrushColor(string brushKey) =>
        Application.Current.Resources[brushKey] is SolidColorBrush brush ? brush.Color : null;

    private FrameworkElement? _root;
    private DispatcherQueue? _dispatcher;
    private IReadOnlyDictionary<string, string>? _light;
    private IReadOnlyDictionary<string, string>? _dark;
    private string _appliedFingerprint = "";

    // Wallpaper-derived app accent (M10 polish): the wallpaper's most-abundant colour is
    // washed subtly into the app-wide surface and the Home widgets scrim, theme-aware.
    private string? _wallpaperId;
    private Color? _dominant;
    private Color _baseSurface = Color.FromArgb(255, 0, 0, 0);
    private Color _baseContainer = Color.FromArgb(255, 0x1F, 0x1F, 0x1F);

    public void Start(FrameworkElement root)
    {
        if (_root is not null)
        {
            return;
        }
        _root = root;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _baseSurface = BrushColor("SurfaceBrush") ?? Color.FromArgb(255, 0, 0, 0);
        _baseContainer = BrushColor("SurfaceContainerBrush") ?? _baseContainer;
        supervisor.StatusUpdated += status => _dispatcher.TryEnqueue(() => OnStatusUpdated(status));
        // M18 B1: turning "Use my phone's colours" off re-derives immediately rather than waiting
        // for the next status. B4: this only rewrites brush COLORS on existing brush instances, so
        // no view model is rebuilt, no view is reloaded and the wallpaper image is never dropped —
        // WinUI animates the brush change in place and there is no white frame.
        registry.UsePhoneColoursChanged += () => _dispatcher.TryEnqueue(ApplyWallpaperAccent);
        supervisor.StateChanged += () => _dispatcher.TryEnqueue(OnLinkStateChanged);
        // The wallpaper is kept on this PC per phone (the owner: "no need to fetch it each
        // time"): it shows at once on start and on a tab switch, connected or not, and is only
        // fetched again when the phone says it changed.
        registry.ActiveDeviceChanged += () => _dispatcher.TryEnqueue(() => _ = ShowCachedWallpaperAsync());
        _ = ShowCachedWallpaperAsync();
        // A Windows theme flip re-resolves the brushes' original ThemeResource
        // colors; re-apply the phone palette (and wallpaper accent) on top afterwards.
        root.ActualThemeChanged += (_, _) =>
        {
            // The bases are local values by now, so they don't follow the flip by themselves:
            // start from the new theme's shipped ramp, which the phone palette (Apply) replaces.
            var neutral = DynamicPalette.Neutral(root.ActualTheme == ElementTheme.Dark);
            _baseSurface = ToColor(neutral.Surface);
            _baseContainer = ToColor(neutral.SurfaceContainer);
            _appliedFingerprint = "";
            Apply();
            ApplyWallpaperAccent();
        };
    }

    private void OnLinkStateChanged()
    {
        // A dropped link keeps the wallpaper: it is this phone's, cached, and still true.
    }

    private async Task ShowCachedWallpaperAsync()
    {
        if (registry.PairedSerial is not { } serial)
        {
            return;
        }
        var bytes = cache.LoadWallpaper(serial);
        var id = cache.LoadWallpaperId(serial);
        if (bytes is not { Length: > 0 })
        {
            return;
        }
        var (image, ambient, dominant) = await DecodeAsync(bytes);
        if (registry.PairedSerial != serial || image is null)
        {
            return; // switched again meanwhile
        }
        _wallpaperId = id;
        Wallpaper = image;
        AmbientWallpaper = ambient;
        _dominant = dominant;
        ApplyWallpaperAccent();
        WallpaperChanged?.Invoke();
    }

    private void OnStatusUpdated(DeviceStatus status)
    {
        if (status.ThemeLight is not null || status.ThemeDark is not null)
        {
            _light = status.ThemeLight;
            _dark = status.ThemeDark;
            Apply();
        }

        // Wallpaper accent is independent of the palette (needs v8 + the All-files grant).
        if (status.WallpaperId != _wallpaperId)
        {
            _wallpaperId = status.WallpaperId;
            if (_wallpaperId is null)
            {
                // The phone can't share it right now (no All-files access): keep the cached one.
            }
            else
            {
                _ = LoadWallpaperAsync(_wallpaperId);
            }
        }
    }

    private async Task LoadWallpaperAsync(string id)
    {
        var serial = registry.PairedSerial;
        var (image, ambient, dominant, bytes) = await FetchWallpaperAsync(id);
        if (_wallpaperId != id || image is null)
        {
            return; // superseded by a newer wallpaper, or it couldn't be read (keep what shows)
        }
        if (serial is not null && bytes is not null) cache.SaveWallpaper(serial, id, bytes);
        Wallpaper = image;
        AmbientWallpaper = ambient;
        _dominant = dominant;
        ApplyWallpaperAccent();
        WallpaperChanged?.Invoke();
    }

    private async Task<(BitmapImage?, BitmapImage?, Color?, byte[]?)> FetchWallpaperAsync(string id)
    {
        try
        {
            var bytes = await connection.FetchBulkAsync("wallpaper", id, CancellationToken.None);
            if (bytes is not { Length: > 0 })
            {
                return (null, null, null, null);
            }
            var (image, ambient, dominant) = await DecodeAsync(bytes);
            return (image, ambient, dominant, bytes);
        }
        catch (Exception)
        {
            return (null, null, null, null); // cosmetic only
        }
    }

    /// <summary>The wallpaper at full size, the same decoded tiny for the ambient blur, and its accent.</summary>
    private static async Task<(BitmapImage?, BitmapImage?, Color?)> DecodeAsync(byte[] bytes)
    {
        try
        {
            var dominant = await ComputeDominantAsync(bytes);
            var image = await ImageAsync(bytes, 0);
            var ambient = await ImageAsync(bytes, AmbientEdge);
            return (image, ambient, dominant);
        }
        catch (Exception)
        {
            return (null, null, null);
        }
    }

    private static async Task<BitmapImage> ImageAsync(byte[] bytes, int decodeWidth)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
        stream.Seek(0);
        var image = new BitmapImage();
        if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
        await image.SetSourceAsync(stream);
        return image;
    }

    /// <summary>Pixels across for the ambient copy: small enough that scaling it up is the blur.</summary>
    private const int AmbientEdge = 36;

    /// <summary>
    /// The wallpaper's most-abundant colour, preferring a vivid one over a flat grey.
    /// <para>
    /// M15b Part E: this walks every pixel, which was free when the phone sent a 48 px image and
    /// is not now that it sends up to 1600. The decode is therefore asked for a bounded
    /// <see cref="DominantScanEdge"/>-px version — the decoder does the downscale, and the answer
    /// is a dominant colour either way. Only this scan is bounded; the image the UI renders is
    /// still decoded at full size.
    /// </para>
    /// </summary>
    private static async Task<Color?> ComputeDominantAsync(byte[] bytes)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var scale = Math.Min(1.0, DominantScanEdge / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * scale),
                ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale),
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            var data = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            var px = data.DetachPixelData();

            var all = new Dictionary<int, int>();   // quantised colour -> count
            var vivid = new Dictionary<int, int>();  // same, restricted to colourful mid-tones
            for (var i = 0; i + 3 < px.Length; i += 4)
            {
                int b = px[i], g = px[i + 1], r = px[i + 2];
                var key = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4); // 12-bit bucket
                all[key] = all.GetValueOrDefault(key) + 1;

                int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                var sat = max == 0 ? 0 : (max - min) / (double)max;
                if (sat > 0.22 && max is > 40 and < 240)
                {
                    vivid[key] = vivid.GetValueOrDefault(key) + 1;
                }
            }
            if (all.Count == 0)
            {
                return null;
            }
            var total = px.Length / 4;
            var pick = vivid.Count > 0 && vivid.Values.Max() > total * 0.04
                ? vivid.MaxBy(kv => kv.Value).Key
                : all.MaxBy(kv => kv.Value).Key;
            // Un-quantise to the bucket centre.
            byte R = (byte)(((pick >> 8) & 0xF) * 17), G = (byte)(((pick >> 4) & 0xF) * 17), B = (byte)((pick & 0xF) * 17);
            return Color.FromArgb(255, R, G, B);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Apply()
    {
        if (_root is null)
        {
            return;
        }
        var palette = _root.ActualTheme == ElementTheme.Dark ? _dark : _light;
        if (palette is null)
        {
            return;
        }
        var fingerprint = string.Join(";", palette.Select(pair => $"{pair.Key}={pair.Value}"));
        if (fingerprint == _appliedFingerprint)
        {
            return; // status arrives every 15 s; only retint when the palette changed
        }
        foreach (var (role, hex) in palette)
        {
            if (RoleToBrush.TryGetValue(role, out var brushKey) &&
                Application.Current.Resources[brushKey] is SolidColorBrush brush &&
                TryParseHex(hex, out var color))
            {
                brush.Color = color;
                if (role == "surface")
                {
                    _baseSurface = color; // capture the neutral base before the wallpaper wash
                }
            }
            // The frosted acrylic and the overlays follow the surfaceContainer role too
            // (retinted in ApplyWallpaperAccent below, on top of this base).
            if (role == "surfaceContainer" && TryParseHex(hex, out var frostColor))
            {
                _baseContainer = frostColor;
            }
        }
        _appliedFingerprint = fingerprint;
        ApplyWallpaperAccent(); // re-layer the wash on top of the refreshed base surface
        PaletteChanged?.Invoke();
    }

    /// <summary>
    /// How strongly the Home wallpaper is veiled, 0.0 (no scrim at all — the wallpaper is fully
    /// sharp and fully visible) to 1.0 (the opaque wash that was there before M15b).
    /// <para>
    /// <b>This is the one number to change if text over the wallpaper becomes hard to read.</b>
    /// It is <c>ScrimStrength</c> in <c>DESKTOP\Linc.Desktop\Services\ThemeSyncService.cs</c>.
    /// 0.0 is the owner's request in M15b E4. Try 0.35 for a light veil, 0.6 for a strong one;
    /// the pre-M15b appearance is 1.0.
    /// </para>
    /// <para>
    /// Part E removed the blur, and the blur was doing double duty as legibility protection —
    /// so this is deliberately a named, documented dial rather than a silent 0, and nothing
    /// leaves a scrim on without saying so.
    /// </para>
    /// </summary>
    public const double ScrimStrength = 0.0;

    /// <summary>
    /// Longest edge the dominant-colour scan decodes to. Small on purpose: it answers "what
    /// colour is this mostly", which does not need 1600 px (M15b Part E).
    /// </summary>
    private const int DominantScanEdge = 96;

    /// <summary>
    /// M18 Part B: derive the surface roles from the wallpaper's dominant colour, through
    /// <see cref="DynamicPalette"/>, which guarantees the WCAG floor by construction (B3).
    ///
    /// <para>The dominant colour is used only when the user has left "Use my phone's colours" on
    /// AND a wallpaper actually arrived. Any of "setting off", "phone disconnected" (which nulls
    /// <c>_dominant</c> in <see cref="OnLinkStateChanged"/>) or "no wallpaper" lands on the same
    /// single code path below with a null dominant — the neutral ramp is the base state, not a
    /// branch (B1).</para>
    ///
    /// <para>B4 — no flicker: this mutates the Color of brushes that are already in the resource
    /// dictionary and already bound. Nothing is re-created, no view model is re-constructed, and
    /// <c>Wallpaper</c> is untouched here, so a palette change cannot flash white or drop the
    /// wallpaper for a frame.</para>
    /// </summary>
    private void ApplyWallpaperAccent()
    {
        var isDark = _root?.ActualTheme == ElementTheme.Dark;

        // The one gate. Everything downstream sees either a colour or null.
        var effective = registry.UsePhoneColours ? _dominant : null;
        var derived = DynamicPalette.Derive(
            effective is { } d ? new Rgb(d.R, d.G, d.B) : null, isDark);

        if (Application.Current.Resources["SurfaceBrush"] is SolidColorBrush surface)
        {
            // Keep the base surface the phone palette supplied, tinted through the clamp so the
            // result can never fall below the contrast floor.
            surface.Color = effective is { } dom
                ? ClampAgainstText(Blend(_baseSurface, dom, DynamicPalette.MaxTint), isDark)
                : _baseSurface;
        }
        if (Application.Current.Resources["OnSurfaceVariantBrush"] is SolidColorBrush caption)
        {
            // The muted caption colour is the one that historically went grey-on-grey over a
            // tinted surface, so it is clamped against the surface actually in use.
            caption.Color = ToColor(derived.OnSurfaceVariant);
        }
        if (Application.Current.Resources["WallpaperScrimBrush"] is SolidColorBrush scrim)
        {
            var fogBase = isDark ? Color.FromArgb(255, 0x10, 0x10, 0x14) : Color.FromArgb(255, 0xFA, 0xFA, 0xFC);
            var tinted = _dominant is { } dom ? Blend(fogBase, dom, 0.22) : fogBase;
            // The full-strength alpha this used to apply unconditionally, now scaled by the dial
            // above. At ScrimStrength 0 the brush is fully transparent, so the Border in
            // HomePage.xaml still exists and still binds — it simply paints nothing.
            var fullAlpha = isDark ? 0xCC : 0xDA;
            var alpha = (byte)Math.Clamp(fullAlpha * ScrimStrength, 0, 255);
            scrim.Color = Color.FromArgb(alpha, tinted.R, tinted.G, tinted.B);
        }

        // Frosted glass (the floating bar, Home's rail cards) and every overlay: the container
        // colour, washed toward the wallpaper and clamped exactly like the surface above.
        var glass = effective is { } wash
            ? ClampAgainstText(Blend(_baseContainer, wash, DynamicPalette.MaxTint), isDark)
            : _baseContainer;
        if (Application.Current.Resources["FrostedSurfaceBrush"] is AcrylicBrush frost)
        {
            frost.TintColor = glass;
            frost.FallbackColor = glass;
        }
        foreach (var key in OverlayBrushKeys)
        {
            switch (Application.Current.Resources[key])
            {
                case AcrylicBrush acrylic:
                    acrylic.TintColor = glass;
                    acrylic.FallbackColor = glass;
                    break;
                case SolidColorBrush glaze:
                    glaze.Color = glass;
                    break;
            }
        }
    }

    /// <summary>
    /// Holds a tinted surface to <see cref="DynamicPalette.BodyContrast"/> against the theme's body
    /// text, walking it back toward the theme's own extreme until it clears. This is the same rule
    /// <c>tools\palettesim</c> sweeps the whole colour cube against.
    /// </summary>
    private static Color ClampAgainstText(Color surface, bool isDark)
    {
        var neutral = DynamicPalette.Neutral(isDark);
        var clamped = DynamicPalette.ClampToContrast(
            new Rgb(surface.R, surface.G, surface.B),
            neutral.OnSurface,
            isDark ? new Rgb(0, 0, 0) : new Rgb(255, 255, 255),
            DynamicPalette.BodyContrast);
        return ToColor(clamped);
    }

    private static Color ToColor(Rgb rgb) => Color.FromArgb(255, rgb.R, rgb.G, rgb.B);

    /// <summary>Linear blend: t of b over a.</summary>
    private static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        255,
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    private static bool TryParseHex(string hex, out Windows.UI.Color color)
    {
        color = default;
        if (hex.Length != 7 || hex[0] != '#' ||
            !uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var rgb))
        {
            return false;
        }
        color = Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }
}
