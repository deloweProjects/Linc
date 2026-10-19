using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Linc.Desktop.Views;

/// <summary>
/// <c>views:ChoiceButton.IsChoice="True"</c> on a ToggleButton that is one option of a group whose
/// selection the view model owns (Ring / Vibrate / Silent, Home's tabs). A ToggleButton checks
/// itself when clicked, before its command runs; with a OneWay <c>IsChecked</c> binding nothing
/// unchecks it again if the view model's answer doesn't change — so when the phone refused a
/// sound mode, every clicked button stayed selected (the owner: "I managed to click everything").
/// This undoes the click's own toggle at once; the command then runs, and the binding checks
/// whichever option the view model reports.
/// </summary>
public static class ChoiceButton
{
    public static readonly DependencyProperty IsChoiceProperty =
        DependencyProperty.RegisterAttached("IsChoice", typeof(bool), typeof(ChoiceButton), new PropertyMetadata(false, OnIsChoiceChanged));

    public static bool GetIsChoice(DependencyObject o) => (bool)o.GetValue(IsChoiceProperty);
    public static void SetIsChoice(DependencyObject o, bool value) => o.SetValue(IsChoiceProperty, value);

    private static void OnIsChoiceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ToggleButton toggle && e.NewValue is true && e.OldValue is not true)
        {
            // Click is raised after the toggle and before the command executes.
            toggle.Click += (_, _) => toggle.IsChecked = toggle.IsChecked != true;
        }
    }
}
