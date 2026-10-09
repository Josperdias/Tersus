namespace Tersus.Core.Scanning;

public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;

    public double Right => X + Width;

    public double Bottom => Y + Height;

    public bool Contains(double px, double py) => px >= X && px < Right && py >= Y && py < Bottom;
}

public readonly record struct TreemapItem(string Key, double Weight);

public readonly record struct TreemapCell(string Key, double Weight, RectD Rect);

/// <summary>
/// Squarified treemap layout (Bruls, Huizing, van Wijk): rectangles with areas proportional to the weights and aspect
/// ratios as close to 1 as possible, so sizes are comparable at a glance. Pure geometry: no UI types, fully testable.
/// </summary>
public static class Treemap
{
    public static IReadOnlyList<TreemapCell> Layout(IEnumerable<TreemapItem> items, RectD bounds)
    {
        var sorted = items.Where(i => i.Weight > 0 && double.IsFinite(i.Weight)).OrderByDescending(i => i.Weight).ToList();
        var cells = new List<TreemapCell>(sorted.Count);
        if (sorted.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return cells;
        }

        double total = sorted.Sum(i => i.Weight);
        double scale = bounds.Area / total;
        RectD free = bounds;
        int index = 0;

        while (index < sorted.Count)
        {
            double side = Math.Min(free.Width, free.Height);
            var row = new List<TreemapItem> { sorted[index] };
            double rowArea = sorted[index].Weight * scale;
            double worst = WorstRatio(rowArea, rowArea, rowArea, side);
            double minArea = rowArea;
            double maxArea = rowArea;
            int next = index + 1;

            while (next < sorted.Count)
            {
                double a = sorted[next].Weight * scale;
                double newSum = rowArea + a;
                double newMax = Math.Max(maxArea, a);
                double newMin = Math.Min(minArea, a);
                double candidate = WorstRatio(newSum, newMin, newMax, side);
                if (candidate > worst)
                {
                    break;
                }

                row.Add(sorted[next]);
                rowArea = newSum;
                minArea = newMin;
                maxArea = newMax;
                worst = candidate;
                next++;
            }

            free = PlaceRow(row, rowArea, scale, free, cells);
            index = next;
        }

        return cells;
    }

    private static double WorstRatio(double sum, double min, double max, double side)
    {
        if (sum <= 0 || side <= 0)
        {
            return double.PositiveInfinity;
        }

        double s2 = sum * sum;
        double w2 = side * side;
        return Math.Max(w2 * max / s2, s2 / (w2 * min));
    }

    private static RectD PlaceRow(List<TreemapItem> row, double rowArea, double scale, RectD free, List<TreemapCell> cells)
    {
        if (free.Width >= free.Height)
        {
            // Vertical strip on the left, items stacked top to bottom.
            double stripWidth = rowArea / free.Height;
            double y = free.Y;
            foreach (TreemapItem item in row)
            {
                double h = item.Weight * scale / stripWidth;
                cells.Add(new TreemapCell(item.Key, item.Weight, new RectD(free.X, y, stripWidth, h)));
                y += h;
            }

            return new RectD(free.X + stripWidth, free.Y, Math.Max(0, free.Width - stripWidth), free.Height);
        }
        else
        {
            // Horizontal strip on top, items laid out left to right.
            double stripHeight = rowArea / free.Width;
            double x = free.X;
            foreach (TreemapItem item in row)
            {
                double w = item.Weight * scale / stripHeight;
                cells.Add(new TreemapCell(item.Key, item.Weight, new RectD(x, free.Y, w, stripHeight)));
                x += w;
            }

            return new RectD(free.X, free.Y + stripHeight, free.Width, Math.Max(0, free.Height - stripHeight));
        }
    }
}
