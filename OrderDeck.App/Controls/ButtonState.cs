using System.Windows;

namespace OrderDeck.App.Controls;

/// <summary>
/// Düğmelere iliştirilen durum bayrakları.
///
/// <para><see cref="IsBusyProperty"/>: düğmenin başlattığı iş sürüyor. Onu tanıyan stiller
/// (<c>OD.Button.Primary</c>, <c>OD.Button.Secondary</c>, <c>OD.Button.Ghost</c>) içeriği gizleyip
/// yerinde <see cref="BusyRing"/> gösterir; düğmenin ölçüsü ve <c>Content</c>'i değişmez, düğme fare
/// tıklaması almaz ve devre dışı görünümüne düşmez. Öbür stiller bayrağı yok sayar.</para>
///
/// <para>Meşgul görünümü şablonun kendi tetikleyicisidir: bayrak şablon uygulanmadan önce de sonra da
/// verilse sonuç aynıdır. Klavyeyle yeniden tetiklenmeyi kesmek tüketen komutun işidir.</para>
/// </summary>
public static class ButtonState
{
    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.RegisterAttached(
        "IsBusy", typeof(bool), typeof(ButtonState), new FrameworkPropertyMetadata(false));

    public static bool GetIsBusy(DependencyObject element) => (bool)element.GetValue(IsBusyProperty);

    public static void SetIsBusy(DependencyObject element, bool value) => element.SetValue(IsBusyProperty, value);
}
