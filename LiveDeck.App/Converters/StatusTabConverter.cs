using System;
using System.Globalization;
using System.Windows.Data;

namespace LiveDeck.App.Converters;

/// <summary>
/// Two-way binds an "active tab" string to a RadioButton's IsChecked. The ConverterParameter
/// is the tab's status value; IsChecked is true iff ActiveTab equals the parameter.
/// </summary>
public sealed class StatusTabConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string current && parameter is string p && current == p;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && parameter is string p ? p : Binding.DoNothing;
}
