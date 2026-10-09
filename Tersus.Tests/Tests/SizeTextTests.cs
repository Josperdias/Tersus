using System.Globalization;
using Tersus.Core;
using Tersus.Tests.Framework;

namespace Tersus.Tests;

[Category("util")]
public class SizeTextTests
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    [Test]
    public void Formats_small_values_as_bytes()
    {
        Assert.Equal("0 B", SizeText.Format(0, PtBr));
        Assert.Equal("1 B", SizeText.Format(1, PtBr));
        Assert.Equal("1023 B", SizeText.Format(1023, PtBr));
    }

    [Test]
    public void Uses_three_significant_digits_and_portuguese_decimal_comma()
    {
        Assert.Equal("1,00 KB", SizeText.Format(1024, PtBr));
        Assert.Equal("1,50 KB", SizeText.Format(1536, PtBr));
        Assert.Equal("14,6 KB", SizeText.Format((long)(14.6 * 1024), PtBr));
        Assert.Equal("146 KB", SizeText.Format(146 * 1024, PtBr));
        Assert.Equal("1,46 GB", SizeText.Format((long)(1.46 * 1024 * 1024 * 1024), PtBr));
        Assert.Equal("256 MB", SizeText.Format(256L * 1024 * 1024, PtBr));
    }

    [Test]
    public void Never_prints_1024_of_a_unit()
    {
        Assert.Equal("1,00 MB", SizeText.Format((1024 * 1024) - 1, PtBr));
        Assert.Equal("1,00 GB", SizeText.Format((1024L * 1024 * 1024) - 1, PtBr));
    }

    [Test]
    public void Handles_negative_and_extreme_values()
    {
        Assert.Equal("-1,00 KB", SizeText.Format(-1024, PtBr));
        Assert.Equal("-5 B", SizeText.Format(-5, PtBr));
        Assert.True(SizeText.Format(long.MaxValue, PtBr).EndsWith("EB", StringComparison.Ordinal));
        Assert.True(SizeText.Format(long.MinValue, PtBr).StartsWith('-') || SizeText.Format(long.MinValue, PtBr).EndsWith("EB", StringComparison.Ordinal));
    }

    [Test]
    public void Exact_format_uses_thousands_separators()
    {
        Assert.Equal("1.234.567 bytes", SizeText.FormatExact(1234567, PtBr));
    }
}
