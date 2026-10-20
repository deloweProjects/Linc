using Linc.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Linc.Desktop.Views;

/// <summary>
/// Settings › Appearance › Font. "default" is Windows' own font and touches nothing at all — the
/// app looks exactly as it always has. The others are OFL fonts bundled in Assets/Fonts (licences
/// beside them), loaded the same ms-appx way as the app's other assets.
/// <para>
/// Applied two ways, because WinUI has no single app-wide font switch: the
/// <c>ContentControlThemeFontFamily</c> resource every control style reads (so controls built
/// later pick it up), and a walk of the live window that retargets what is already on screen
/// (the title bar, the floating bar, the open page) so a change shows at once. Only text in
/// Windows' default font, or in the font this class applied before, is retargeted: Consolas, the
/// icon font and anything a page chose on purpose keep their own.
/// </para>
/// </summary>
public static class AppFonts
{
    private const string ResourceKey = "ContentControlThemeFontFamily";

    /// <summary>The bundled family for a Settings key; null for Windows' own font.</summary>
    public static string? SourceFor(string key) => key switch
    {
        "inter" => "ms-appx:///Assets/Fonts/Inter.ttf#Inter",
        "jakarta" => "ms-appx:///Assets/Fonts/PlusJakartaSans.ttf#Plus Jakarta Sans",
        "manrope" => "ms-appx:///Assets/Fonts/Manrope.ttf#Manrope",
        "outfit" => "ms-appx:///Assets/Fonts/Outfit.ttf#Outfit",
        _ => null,
    };

    /// <summary>On an element, keeps it and everything inside it out of the walk (the Font previews).</summary>
    public static readonly DependencyProperty KeepProperty =
        DependencyProperty.RegisterAttached("Keep", typeof(bool), typeof(AppFonts), new PropertyMetadata(false));

    public static bool GetKeep(DependencyObject o) => (bool)o.GetValue(KeepProperty);
    public static void SetKeep(DependencyObject o, bool value) => o.SetValue(KeepProperty, value);

    /// <summary>The family source currently applied; null = Windows' own (nothing applied).</summary>
    private static string? _applied;

    /// <summary>
    /// Sets the resource the control styles read. Call before the main window is built, so on a
    /// non-default font every control starts in it; with the default font this does nothing.
    /// </summary>
    public static void ApplyResource(string key)
    {
        var source = SourceFor(key);
        var resources = Application.Current.Resources;
        if (source is null)
        {
            // Back to Windows' own: drop our top-level override and the merged default shows again.
            if (_applied is not null) resources.Remove(ResourceKey);
        }
        else
        {
            resources[ResourceKey] = new FontFamily(source);
        }
    }

    /// <summary>
    /// Applies the saved font to the window now (when it isn't the default) and again whenever
    /// Settings changes it.
    /// </summary>
    public static void Attach(Window window, IDeviceRegistry registry)
    {
        if (window.Content is not FrameworkElement root) return;
        void Apply()
        {
            var source = SourceFor(registry.AppFont);
            if (source == _applied) return; // AppearanceChanged also fires for the other appearance settings
            ApplyResource(registry.AppFont);
            var previous = _applied;
            _applied = source;
            Walk(root, source is null ? null : new FontFamily(source), previous);
        }
        if (SourceFor(registry.AppFont) is not null)
        {
            if (root.IsLoaded) Apply();
            else root.Loaded += (_, _) => Apply();
        }
        registry.AppearanceChanged += () => root.DispatcherQueue.TryEnqueue(Apply);
    }

    private static void Walk(DependencyObject node, FontFamily? family, string? previous)
    {
        if (GetKeep(node)) return;
        switch (node)
        {
            case Control control:
                if (Retargets(control.FontFamily, previous))
                {
                    // A control's own font comes from its style, which read the resource when it
                    // was built — so going back to the default means saying so, not clearing.
                    control.FontFamily = family ?? FontFamily.XamlAutoFontFamily;
                }
                break;
            case TextBlock text:
                if (Retargets(text.FontFamily, previous))
                {
                    if (family is null) text.ClearValue(TextBlock.FontFamilyProperty); // inherit again
                    else text.FontFamily = family;
                }
                break;
        }
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            Walk(VisualTreeHelper.GetChild(node, i), family, previous);
        }
    }

    /// <summary>Windows' default text font, or the one applied last time; never icons or code.</summary>
    private static bool Retargets(FontFamily? current, string? previous)
    {
        var source = current?.Source ?? "";
        if (previous is not null && source == previous) return true;
        if (source == "XamlAutoFontFamily") return true;
        return source.StartsWith("Segoe UI", StringComparison.OrdinalIgnoreCase)
            && !source.Contains("Symbol", StringComparison.OrdinalIgnoreCase)
            && !source.Contains("Emoji", StringComparison.OrdinalIgnoreCase)
            && !source.Contains(',');
    }
}
