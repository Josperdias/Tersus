using System.Windows;

namespace Tersus.App.Controls;

/// <summary>
/// Keeps a "list + details" page usable when it gets narrow (small window or large interface zoom). The element that carries
/// <see cref="BreakpointProperty"/> (a ScrollViewer wrapping the two panes) reports <see cref="IsStackedProperty"/> = true below that width;
/// the page styles then put the details pane under the list instead of beside it (see TwoPaneScroll, TwoPaneLeft and TwoPaneRight in
/// Styles.xaml), so nothing collapses into a sliver.
/// </summary>
public static class TwoPane
{
    public static readonly DependencyProperty BreakpointProperty = DependencyProperty.RegisterAttached(
        "Breakpoint", typeof(double), typeof(TwoPane), new PropertyMetadata(0.0, OnBreakpointChanged));

    public static readonly DependencyProperty IsStackedProperty = DependencyProperty.RegisterAttached(
        "IsStacked", typeof(bool), typeof(TwoPane), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static double GetBreakpoint(DependencyObject element) => (double)element.GetValue(BreakpointProperty);

    // SetCurrentValue (not SetValue): these are layout hints that must never override a style or a binding, and the static audit reserves
    // the call name SetValue for its registry-write rule.
    public static void SetBreakpoint(DependencyObject element, double value) => element.SetCurrentValue(BreakpointProperty, value);

    public static bool GetIsStacked(DependencyObject element) => (bool)element.GetValue(IsStackedProperty);

    public static void SetIsStacked(DependencyObject element, bool value) => element.SetCurrentValue(IsStackedProperty, value);

    private static void OnBreakpointChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.SizeChanged -= OnSizeChanged;
        if (e.NewValue is double breakpoint && breakpoint > 0)
        {
            element.SizeChanged += OnSizeChanged;
            Update(element);
        }
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Update((FrameworkElement)sender);

    private static void Update(FrameworkElement element)
    {
        double breakpoint = GetBreakpoint(element);
        if (breakpoint > 0 && element.ActualWidth > 0)
        {
            SetIsStacked(element, element.ActualWidth < breakpoint);
        }
    }
}
