using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Tersus.Core;
using Tersus.Core.Knowledge;

namespace Tersus.App.Services;

public sealed class BytesToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            long l => SizeText.Format(l, culture),
            int i => SizeText.Format(i, culture),
            _ => string.Empty,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool invert = parameter as string == "invert";
        bool visible = value is not null && !(value is string s && s.Length == 0);
        return visible != invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b ? !b : true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b ? !b : false;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool invert = parameter as string == "invert";
        bool any = value is int i && i > 0;
        return any != invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class LocalDateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTime d ? d.ToLocalTime().ToString("dd/MM/yyyy HH:mm", culture) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class RiskToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value is RiskLevel r ? r switch
        {
            RiskLevel.Protected => "RiskProtected",
            RiskLevel.Caution => "RiskCaution",
            RiskLevel.Low => "RiskLow",
            _ => "RiskInfo",
        } : "RiskInfo";
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.DimGray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class RiskToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is RiskLevel r ? Describe(r) : string.Empty;

    public static string Describe(RiskLevel r) => r switch
    {
        RiskLevel.Protected => "PROTEGIDO — o Tersus nunca oferece limpar isto",
        RiskLevel.Caution => "CUIDADO — revise manualmente",
        RiskLevel.Low => "RISCO BAIXO — ainda assim, só revisão",
        _ => "INFORMAÇÃO",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class ConfidenceToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Confidence c ? c switch
        {
            Confidence.High => "Confiança alta",
            Confidence.Medium => "Confiança média",
            _ => "Confiança baixa (não inferimos além do que sabemos)",
        } : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value is Tersus.Core.Advice.AdviceSeverity s ? s switch
        {
            Tersus.Core.Advice.AdviceSeverity.Important => "RiskProtected",
            Tersus.Core.Advice.AdviceSeverity.Attention => "RiskCaution",
            _ => "RiskInfo",
        } : "RiskInfo";
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.DimGray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Turns a resource key ("IconHome") into the Geometry stored under that key in the application resources.</summary>
public sealed class ResourceKeyToGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key ? Application.Current?.TryFindResource(key) as Geometry : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
