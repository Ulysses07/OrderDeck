using System;
using System.Globalization;
using System.Windows.Data;

namespace OrderDeck.App.Converters;

/// <summary>
/// İki bağlamayı karşılaştırır, eşitse true döner.
///
/// NEDEN VAR: <c>DataTrigger.Value</c> bir DependencyProperty değil, oraya
/// Binding konamıyor. "Şu düğmenin anahtarı açık sayfanınkiyle aynı mı"
/// sorusunu sormanın tek yolu karşılaştırmayı bir MultiBinding'e taşımak.
///
/// Kardeşi <see cref="EqualityToBrushConverter"/> aynı işi yapıp doğrudan
/// fırça döndürüyor, ama fırçaları ConverterParameter'da HEX olarak alıyor —
/// spec §10 "sabit hex 0" hedefiyle çelişiyor. Bu sürüm bool döndürüp rengi
/// tetikleyicideki token'a bırakıyor.
/// </summary>
public sealed class EqualsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values is { Length: >= 2 } && Equals(values[0], values[1]);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
