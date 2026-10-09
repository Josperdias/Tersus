using System.Globalization;

namespace Tersus.App.Services;

/// <summary>Portuguese singular/plural for the counts shown in the interface ("1 arquivo", "2 arquivos").</summary>
public static class Plural
{
    public static string Of(long count, string singular, string plural, CultureInfo? culture = null) =>
        string.Create(culture ?? CultureInfo.CurrentCulture, $"{count:N0} {(count == 1 ? singular : plural)}");

    public static string Files(long count, CultureInfo? culture = null) => Of(count, "arquivo", "arquivos", culture);

    public static string Folders(long count, CultureInfo? culture = null) => Of(count, "pasta", "pastas", culture);
}
