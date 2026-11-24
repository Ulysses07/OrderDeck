namespace OrderDeck.Core.Chat;

/// <summary>
/// "Sohbeti yenile" isteği. Sohbet servisleri (YouTube, Facebook, Instagram) ve uzantı köprüsü, yayın
/// oturumu olaylarını dinledikleri gibi bunu dinler; kabuk <see cref="Request"/> çağırır.
///
/// <para><b>Dinleyici sözleşmesi.</b> <see cref="Requested"/> çağıranın iş parçacığında (arayüz)
/// yükselir. Dinleyici BEKLETMEZ: yalnız iptal eder, uyandırır, alan yazar; yeniden bağlanmanın
/// kendisi servisin kendi döngüsünde sürer.</para>
///
/// <para>Neden servisleri doğrudan çağırmıyoruz: servisler barındırılan servis olarak fabrika
/// lambdasıyla kayıtlı, somut tipleriyle çözülemiyor; oturum servisindeki olay deseni zaten bu
/// ihtiyaç için kullanılıyor.</para>
/// </summary>
public sealed class ChatReconnectSignal
{
    /// <summary>Yenileme istendi.</summary>
    public event EventHandler? Requested;

    /// <summary>
    /// Bütün dinleyicileri çağırır. Biri istisna atarsa öbürleri yine çağrılır ve istisna yutulur:
    /// bir kaynağın bozuk dinleyicisi öbür kaynakların yenilenmesini engellememeli, operatörün
    /// tıklamasına da sıçramamalı.
    /// </summary>
    public void Request()
    {
        if (Requested is not { } handlers) return;

        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((EventHandler)handler)(this, EventArgs.Empty); }
            catch { /* dinleyicinin hatası kendi kaynağında kalır */ }
        }
    }
}
