using System.Windows.Media;

namespace Tersus.App.Controls;

/// <summary>One shared, colour-blind-friendly palette so a folder has the same colour in the treemap and in the list next to it.</summary>
public static class Palette
{
    private static readonly Color[] Colors =
    [
        Color.FromRgb(0x22, 0x65, 0xD1), Color.FromRgb(0x0F, 0x7B, 0x5F), Color.FromRgb(0xB4, 0x53, 0x09), Color.FromRgb(0x7B, 0x3F, 0xA0),
        Color.FromRgb(0xC2, 0x18, 0x5B), Color.FromRgb(0x00, 0x83, 0x8F), Color.FromRgb(0x5D, 0x40, 0x37), Color.FromRgb(0x39, 0x49, 0xAB),
        Color.FromRgb(0x55, 0x75, 0x1B), Color.FromRgb(0x8E, 0x24, 0xAA),
    ];

    private static readonly Brush[] Brushes = [.. Colors.Select(c => Freeze(new SolidColorBrush(c)))];
    private static readonly Brush OtherBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x5F, 0x6B, 0x7A)));

    public static Brush For(int index, bool isOther = false) => isOther ? OtherBrush : Brushes[index % Brushes.Length];

    private static Brush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }
}
