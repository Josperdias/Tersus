using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Tersus.App.Smoke;

internal static class UiTree
{
    public static IEnumerable<DependencyObject> All(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (DependencyObject d in All(child))
            {
                yield return d;
            }
        }
    }

    public static IEnumerable<T> Of<T>(DependencyObject root)
        where T : DependencyObject => All(root).OfType<T>();

    /// <summary>Elements that were given less room than they asked for (text or a control that is cut off).</summary>
    public static IEnumerable<string> Clipped(DependencyObject root)
    {
        foreach (FrameworkElement fe in All(root).OfType<FrameworkElement>())
        {
            if (!fe.IsVisible || fe.ActualWidth <= 0)
            {
                continue;
            }

            bool interesting = fe is TextBlock tb ? tb.TextTrimming == TextTrimming.None : fe is Button or CheckBox or RadioButton or ComboBox;
            if (!interesting)
            {
                continue;
            }

            Size want = fe.DesiredSize;
            Size got = fe.RenderSize;
            if (got.Width + 1 < want.Width || got.Height + 1 < want.Height)
            {
                yield return $"{Describe(fe)}: pediu {want.Width:F0}×{want.Height:F0}, recebeu {got.Width:F0}×{got.Height:F0}";
            }
        }
    }

    public static string Describe(FrameworkElement fe)
    {
        string text = fe switch
        {
            TextBlock t => t.Text,
            ContentControl c when c.Content is string s => s,
            _ => string.Empty,
        };
        if (text.Length > 48)
        {
            text = text[..48] + "…";
        }

        return $"{fe.GetType().Name}{(string.IsNullOrEmpty(fe.Name) ? string.Empty : "#" + fe.Name)}{(text.Length > 0 ? " “" + text.Replace('\n', ' ') + "”" : string.Empty)}";
    }
}
