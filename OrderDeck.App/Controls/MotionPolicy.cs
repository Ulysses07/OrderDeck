using System.Windows;

namespace OrderDeck.App.Controls;

/// <summary>
/// "Hareket çalışsın mı" sorusunun tek cevap yeri.
///
/// <para>Motion.xaml'ın kuralı: Windows'ta "pencere içi animasyonlar" kapalıysa
/// (<see cref="SystemParameters.ClientAreaAnimation"/> yanlış) geçişler atlanır. Bu, web'deki
/// <c>prefers-reduced-motion</c>'ın karşılığıdır. Koşul XAML'dan sorulamadığı için her animasyon
/// sahibi onu kendi başına yoklardı; tek yerde durunca unutulamaz ve testten değiştirilebilir.</para>
///
/// <para>Değer her soruşta yeniden okunur: ayar uygulama açıkken değişirse sonraki geçiş yeni
/// değere uyar.</para>
/// </summary>
public static class MotionPolicy
{
    /// <summary>Test dikişi: null iken sistem ayarı geçerlidir. Ürün kodu bunu değiştirmez.</summary>
    internal static bool? Override { get; set; }

    /// <summary>Geçişler ve döngüler çalışsın mı.</summary>
    public static bool IsEnabled => Override ?? SystemParameters.ClientAreaAnimation;
}
