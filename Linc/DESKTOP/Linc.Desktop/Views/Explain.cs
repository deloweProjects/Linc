using Linc.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace Linc.Desktop.Views;

/// <summary>
/// Settings → Appearance → "Show explanations" (1.0.0.11, D-084). Mark an explanatory line with
/// <c>views:Explain.IsHelp="True"</c> and it hides while explanations are off. Titles, values,
/// warnings, errors and the reason something is blocked are never marked — they always show.
/// One app setting (<see cref="IDeviceRegistry.ShowExplanations"/>), per app, no sync.
/// </summary>
public static class Explain
{
    public static readonly DependencyProperty IsHelpProperty = DependencyProperty.RegisterAttached(
        "IsHelp", typeof(bool), typeof(Explain), new PropertyMetadata(false, OnIsHelpChanged));

    public static bool GetIsHelp(DependencyObject element) => (bool)element.GetValue(IsHelpProperty);

    public static void SetIsHelp(DependencyObject element, bool value) => element.SetValue(IsHelpProperty, value);

    private static readonly List<WeakReference<FrameworkElement>> Marked = [];
    private static IDeviceRegistry? _registry;

    private static IDeviceRegistry Registry
    {
        get
        {
            if (_registry is null)
            {
                _registry = App.Services.GetRequiredService<IDeviceRegistry>();
                _registry.ShowExplanationsChanged += () =>
                    App.MainWindowInstance?.DispatcherQueue.TryEnqueue(ApplyAll);
            }
            return _registry;
        }
    }

    private static void OnIsHelpChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.NewValue is not true)
        {
            return;
        }
        lock (Marked)
        {
            Marked.RemoveAll(w => !w.TryGetTarget(out _));
            Marked.Add(new WeakReference<FrameworkElement>(element));
        }
        Apply(element);
    }

    private static void Apply(FrameworkElement element) =>
        element.Visibility = Registry.ShowExplanations ? Visibility.Visible : Visibility.Collapsed;

    private static void ApplyAll()
    {
        List<FrameworkElement> alive;
        lock (Marked)
        {
            alive = Marked.Select(w => w.TryGetTarget(out var e) ? e : null).OfType<FrameworkElement>().ToList();
        }
        foreach (var element in alive)
        {
            Apply(element);
        }
    }
}
