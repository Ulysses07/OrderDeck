using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace OrderDeck.App.Converters;

/// <summary>
/// MultiBinding converter: returns the "match" brush when values[0].Equals(values[1]),
/// the "miss" brush otherwise. Used by AnimationPickerControl to highlight the selected
/// card without stuffing a Binding into DataTrigger.Value (which WPF rejects because
/// DataTrigger.Value is not a DependencyProperty).
///
/// ConverterParameter format: "matchHexBrush|missHexBrush" (e.g. "#FFFFCE46|Transparent").
/// </summary>
public sealed class EqualityToBrushConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2) return Brushes.Transparent;

        var (matchBrush, missBrush) = ParseParameter(parameter);
        return Equals(values[0], values[1]) ? matchBrush : missBrush;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static (Brush match, Brush miss) ParseParameter(object parameter)
    {
        // Default: gold border on match, transparent on miss.
        const string fallback = "#FFFFCE46|Transparent";
        var spec = parameter as string ?? fallback;
        var parts = spec.Split('|');
        if (parts.Length != 2) parts = fallback.Split('|');

        return (BrushFromString(parts[0]), BrushFromString(parts[1]));
    }

    private static Brush BrushFromString(string s)
    {
        var bc = new BrushConverter();
        return (Brush?)bc.ConvertFromString(s) ?? Brushes.Transparent;
    }
}
