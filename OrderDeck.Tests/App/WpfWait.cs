using System.Windows;
using System.Windows.Threading;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.App;

/// <summary>
/// Hareketi sınayan testler için bekleme: GERÇEK bir ileti döngüsü çalıştırır.
///
/// <para>NEDEN <see cref="ThemeTestHost.Pump"/> yetmiyor: o, kuyruktaki işleri boşaltıp hemen döner.
/// Animasyon saati ise zamanlayıcı iletileriyle ilerler; kuyruk hiç boş kalmadığı için o iletiler
/// sıraya giremez ve animasyon (süresi sıfır olan durum değişimi bile) hiç uygulanmaz. Ölçüldü:
/// kısa aralıklarla <c>Pump</c> çağıran döngüde değerler yerinden oynamıyor, zamanlayıcıyla biten
/// çerçevede 40 ms'de hedefe varıyor.</para>
///
/// <para>Yalnız <see cref="ThemeTestHost.RunOnSta"/> gövdesinde çağrılır.</para>
/// </summary>
internal static class WpfWait
{
    /// <summary>İleti döngüsünü verilen süre kadar çalıştırır.</summary>
    internal static void For(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// Koşul doğru olana dek ileti döngüsünü 20 ms'lik adımlarla çalıştırır. Süre sabit beklenmez:
    /// yük altındaki makinede de koşul sağlanır sağlanmaz döner (zamana bağlı kırmızı olmasın).
    /// Üst sınır <see cref="TestPatience.Upper"/>: "olması gereken bir şeyi bekle" sınırıdır,
    /// bekleme süresi değil; tam pakette kısa sınırlar aralıklı kırmızı veriyordu.
    /// </summary>
    /// <returns>Koşul süre dolmadan sağlandıysa true.</returns>
    internal static bool Until(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = Environment.TickCount64 + (long)(timeout ?? TestPatience.Upper).TotalMilliseconds;
        while (true)
        {
            if (condition()) return true;
            if (Environment.TickCount64 >= deadline) return false;
            For(20);
        }
    }

    /// <summary>İçeriği ekran dışında, odak almayan bir pencerede gösterir. Çağıran kapatır.</summary>
    internal static Window OffScreenWindow(object content, double width = 300, double height = 120)
    {
        var window = new Window
        {
            Content = content, Width = width, Height = height, Left = -10000, Top = -10000,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None,
        };
        window.Show();
        return window;
    }
}
