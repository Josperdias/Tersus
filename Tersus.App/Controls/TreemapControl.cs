using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Tersus.Core;
using Tersus.Core.Scanning;

namespace Tersus.App.Controls;

/// <summary>One block of the treemap. <see cref="Key"/> ties it to the list row next to it.</summary>
public sealed record TreemapEntry(string Key, string Label, long Bytes, bool IsOther);

/// <summary>
/// Proportional block map (squarified treemap): the area of each block is its share of the folder. The layout maths lives in
/// Tersus.Core (tested); this control only draws and reports clicks. It is a visual aid: the list beside it has the same
/// information and is the keyboard- and screen-reader-accessible way to use it.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IReadOnlyList<TreemapEntry>), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((TreemapControl)d).Rebuild()));

    public static readonly DependencyProperty SelectedKeyProperty = DependencyProperty.Register(
        nameof(SelectedKey), typeof(string), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectCommandProperty = DependencyProperty.Register(
        nameof(SelectCommand), typeof(ICommand), typeof(TreemapControl));

    public static readonly DependencyProperty InvokeCommandProperty = DependencyProperty.Register(
        nameof(InvokeCommand), typeof(ICommand), typeof(TreemapControl));

    private readonly List<Block> _blocks = [];
    private string? _hoverKey;

    private sealed record Block(Rect Rect, int Index, TreemapEntry Entry);

    public TreemapControl()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        Focusable = false;
        ToolTipService.SetInitialShowDelay(this, 250);
        ToolTipService.SetShowDuration(this, 20_000);
    }

    public IReadOnlyList<TreemapEntry>? Items
    {
        get => (IReadOnlyList<TreemapEntry>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public string? SelectedKey
    {
        get => (string?)GetValue(SelectedKeyProperty);
        set => SetValue(SelectedKeyProperty, value);
    }

    /// <summary>Single click: select that block's row.</summary>
    public ICommand? SelectCommand
    {
        get => (ICommand?)GetValue(SelectCommandProperty);
        set => SetValue(SelectCommandProperty, value);
    }

    /// <summary>Double click: open (drill into) that block.</summary>
    public ICommand? InvokeCommand
    {
        get => (ICommand?)GetValue(InvokeCommandProperty);
        set => SetValue(InvokeCommandProperty, value);
    }

    /// <summary>Number of blocks currently laid out (used by the smoke test to prove the map is not blank).</summary>
    public int BlockCount => _blocks.Count;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 480 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 280 : availableSize.Height);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Rebuild();
    }

    private void Rebuild()
    {
        _blocks.Clear();
        IReadOnlyList<TreemapEntry>? items = Items;
        if (items is { Count: > 0 } && ActualWidth > 4 && ActualHeight > 4)
        {
            var byKey = new Dictionary<string, (int Index, TreemapEntry Entry)>(StringComparer.Ordinal);
            for (int i = 0; i < items.Count; i++)
            {
                byKey[items[i].Key] = (i, items[i]);
            }

            IReadOnlyList<TreemapCell> cells = Treemap.Layout(items.Select(e => new TreemapItem(e.Key, e.Bytes)), new RectD(0, 0, ActualWidth, ActualHeight));
            foreach (TreemapCell c in cells)
            {
                if (byKey.TryGetValue(c.Key, out var hit))
                {
                    var r = new Rect(c.Rect.X, c.Rect.Y, Math.Max(0, c.Rect.Width), Math.Max(0, c.Rect.Height));
                    _blocks.Add(new Block(r, hit.Index, hit.Entry));
                }
            }
        }

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        Brush background = TryFindResource("CardBg") as Brush ?? Brushes.White;
        dc.DrawRectangle(background, null, new Rect(0, 0, ActualWidth, ActualHeight));

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        Brush gap = TryFindResource("CardBg") as Brush ?? Brushes.White;
        var gapPen = new Pen(gap, 2);
        gapPen.Freeze();
        Brush navy = TryFindResource("Navy") as Brush ?? Brushes.Black;
        var selectPen = new Pen(navy, 3);
        selectPen.Freeze();
        var hoverPen = new Pen(Brushes.White, 2);
        hoverPen.Freeze();

        foreach (Block b in _blocks)
        {
            Rect r = b.Rect;
            Brush fill = Palette.For(b.Index, b.Entry.IsOther);
            dc.DrawRectangle(fill, gapPen, r);

            if (r.Width >= 74 && r.Height >= 38)
            {
                string size = SizeText.Format(b.Entry.Bytes);
                var text = new FormattedText(
                    b.Entry.Label + "\n" + size,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    12.5,
                    Brushes.White,
                    dpi)
                {
                    MaxTextWidth = Math.Max(10, r.Width - 12),
                    MaxTextHeight = Math.Max(10, r.Height - 10),
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                dc.DrawText(text, new Point(r.X + 6, r.Y + 5));
            }

            if (b.Entry.Key == SelectedKey)
            {
                dc.DrawRectangle(null, selectPen, Rect.Inflate(r, -2, -2));
                dc.DrawRectangle(null, hoverPen, Rect.Inflate(r, -4.5, -4.5));
            }
            else if (b.Entry.Key == _hoverKey)
            {
                dc.DrawRectangle(null, hoverPen, Rect.Inflate(r, -3, -3));
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Block? hit = HitTest(e.GetPosition(this));
        string? key = hit?.Entry.Key;
        if (key == _hoverKey)
        {
            return;
        }

        _hoverKey = key;
        Cursor = hit is null ? Cursors.Arrow : Cursors.Hand;
        if (hit is null)
        {
            ToolTip = null;
        }
        else
        {
            double total = _blocks.Sum(b => (double)b.Entry.Bytes);
            double share = total <= 0 ? 0 : hit.Entry.Bytes / total;
            ToolTip = $"{hit.Entry.Label}\n{SizeText.Format(hit.Entry.Bytes)}  •  {share.ToString("P1", CultureInfo.CurrentCulture)}\nClique para selecionar; duplo clique para abrir.";
        }

        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverKey is not null)
        {
            _hoverKey = null;
            ToolTip = null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Block? hit = HitTest(e.GetPosition(this));
        if (hit is null)
        {
            return;
        }

        ICommand? command = e.ClickCount >= 2 ? InvokeCommand : SelectCommand;
        if (command?.CanExecute(hit.Entry.Key) == true)
        {
            command.Execute(hit.Entry.Key);
        }

        e.Handled = true;
    }

    private Block? HitTest(Point p)
    {
        foreach (Block b in _blocks)
        {
            if (b.Rect.Contains(p))
            {
                return b;
            }
        }

        return null;
    }
}
