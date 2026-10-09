using Tersus.Core.Scanning;
using Tersus.Tests.Framework;

namespace Tersus.Tests;

[Category("scanner")]
public class TreemapTests
{
    private static readonly RectD Box = new(0, 0, 1000, 600);

    private static void CheckGeometry(IReadOnlyList<TreemapCell> cells, RectD bounds, double totalWeight)
    {
        const double eps = 1e-6;
        double areaSum = 0;
        foreach (TreemapCell c in cells)
        {
            Assert.True(c.Rect.X >= bounds.X - eps && c.Rect.Y >= bounds.Y - eps && c.Rect.Right <= bounds.Right + eps && c.Rect.Bottom <= bounds.Bottom + eps, $"cell {c.Key} outside bounds: {c.Rect}");
            Assert.True(c.Rect.Width > 0 && c.Rect.Height > 0, "degenerate cell");
            double expected = c.Weight / totalWeight * bounds.Area;
            Assert.True(Math.Abs(c.Rect.Area - expected) <= (expected * 1e-9) + eps, $"area of {c.Key} not proportional: {c.Rect.Area} vs {expected}");
            areaSum += c.Rect.Area;
        }

        Assert.True(Math.Abs(areaSum - bounds.Area) <= bounds.Area * 1e-9, "cells must tile the whole area");

        for (int i = 0; i < cells.Count; i++)
        {
            for (int j = i + 1; j < cells.Count; j++)
            {
                RectD a = cells[i].Rect;
                RectD b = cells[j].Rect;
                double w = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
                double h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
                Assert.False(w > 1e-6 && h > 1e-6, $"cells {cells[i].Key} and {cells[j].Key} overlap");
            }
        }
    }

    [Test]
    public void Tiles_the_whole_area_proportionally_without_overlap()
    {
        var items = new[] { 600.0, 600, 400, 300, 200, 100, 50, 25, 10, 5, 1 }.Select((w, i) => new TreemapItem("k" + i, w)).ToList();
        IReadOnlyList<TreemapCell> cells = Treemap.Layout(items, Box);
        Assert.Equal(items.Count, cells.Count);
        CheckGeometry(cells, Box, items.Sum(i => i.Weight));
    }

    [Test]
    public void Random_layouts_always_satisfy_the_geometry_invariants()
    {
        var rng = new Random(99);
        for (int round = 0; round < 60; round++)
        {
            int n = rng.Next(1, 40);
            var items = Enumerable.Range(0, n).Select(i => new TreemapItem("i" + i, Math.Pow(10, rng.NextDouble() * 6))).ToList();
            var bounds = new RectD(rng.Next(0, 50), rng.Next(0, 50), rng.Next(100, 1600), rng.Next(100, 1000));
            CheckGeometry(Treemap.Layout(items, bounds), bounds, items.Sum(i => i.Weight));
        }
    }

    [Test]
    public void Equal_weights_in_a_square_give_square_cells()
    {
        var items = Enumerable.Range(0, 16).Select(i => new TreemapItem("k" + i, 1)).ToList();
        IReadOnlyList<TreemapCell> cells = Treemap.Layout(items, new RectD(0, 0, 400, 400));
        foreach (TreemapCell c in cells)
        {
            Assert.True(Math.Abs(c.Rect.Width - 100) < 1e-6 && Math.Abs(c.Rect.Height - 100) < 1e-6, $"{c.Key}: {c.Rect}");
        }
    }

    [Test]
    public void Keeps_aspect_ratios_reasonable_for_typical_disk_data()
    {
        var items = new[] { 500.0, 300, 220, 150, 90, 60, 40, 30, 20, 12 }.Select((w, i) => new TreemapItem("k" + i, w)).ToList();
        IReadOnlyList<TreemapCell> cells = Treemap.Layout(items, Box);
        foreach (TreemapCell c in cells)
        {
            double ratio = Math.Max(c.Rect.Width / c.Rect.Height, c.Rect.Height / c.Rect.Width);
            Assert.True(ratio < 6, $"{c.Key} has an extreme aspect ratio {ratio:0.0}");
        }
    }

    [Test]
    public void Single_item_fills_the_box_and_largest_comes_first()
    {
        IReadOnlyList<TreemapCell> one = Treemap.Layout([new TreemapItem("only", 5)], Box);
        Assert.Equal(1, one.Count);
        Assert.Equal(Box, one[0].Rect);
        IReadOnlyList<TreemapCell> many = Treemap.Layout([new TreemapItem("small", 1), new TreemapItem("big", 9)], Box);
        Assert.Equal("big", many[0].Key);
    }

    [Test]
    public void Ignores_zero_negative_nan_and_infinite_weights_and_empty_boxes()
    {
        IReadOnlyList<TreemapCell> cells = Treemap.Layout(
            [new TreemapItem("a", 0), new TreemapItem("b", -3), new TreemapItem("c", double.NaN), new TreemapItem("d", double.PositiveInfinity), new TreemapItem("e", 2)],
            Box);
        Assert.Equal(1, cells.Count);
        Assert.Equal("e", cells[0].Key);
        Assert.Equal(0, Treemap.Layout([], Box).Count);
        Assert.Equal(0, Treemap.Layout([new TreemapItem("x", 1)], new RectD(0, 0, 0, 10)).Count);
        Assert.Equal(0, Treemap.Layout([new TreemapItem("x", 1)], new RectD(0, 0, 10, -1)).Count);
    }

    [Test]
    public void Survives_extreme_weight_ratios()
    {
        var items = new[] { new TreemapItem("huge", 1e15), new TreemapItem("tiny", 1), new TreemapItem("tiny2", 3) };
        IReadOnlyList<TreemapCell> cells = Treemap.Layout(items, Box);
        Assert.Equal(3, cells.Count);
        Assert.True(cells.All(c => c.Rect.Width > 0 && c.Rect.Height > 0 && double.IsFinite(c.Rect.Area)));
    }
}
