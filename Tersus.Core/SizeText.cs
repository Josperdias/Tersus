using System.Globalization;

namespace Tersus.Core;

/// <summary>
/// Human readable byte sizes. Binary units (1 KB = 1024 B) with Windows-style labels and
/// three significant digits, e.g. "512 B", "1,46 GB", "14,6 GB", "146 GB".
/// Sizes are always LOGICAL sizes; the UI says so explicitly where it matters.
/// </summary>
public static class SizeText
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];

    public static string Format(long bytes, IFormatProvider? provider = null)
    {
        provider ??= CultureInfo.CurrentCulture;
        if (bytes == long.MinValue)
        {
            bytes = long.MaxValue;
        }

        bool negative = bytes < 0;
        double value = Math.Abs((double)bytes);
        string sign = negative ? "-" : string.Empty;

        if (value < 1024)
        {
            return string.Create(provider, $"{sign}{(long)value} B");
        }

        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        int decimals = Decimals(value);
        double rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        if (rounded >= 1024 && unit < Units.Length - 1)
        {
            // e.g. 1023.99 KB must read "1,00 MB", not "1024 KB".
            value = rounded / 1024;
            unit++;
            decimals = Decimals(value);
            rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        }

        string number = rounded.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), provider);
        return $"{sign}{number} {Units[unit]}";
    }

    /// <summary>Exact byte count with thousands separators, e.g. "1.234.567 bytes".</summary>
    public static string FormatExact(long bytes, IFormatProvider? provider = null)
    {
        provider ??= CultureInfo.CurrentCulture;
        return string.Create(provider, $"{bytes:N0} bytes");
    }

    private static int Decimals(double value) => value < 10 ? 2 : value < 100 ? 1 : 0;
}
