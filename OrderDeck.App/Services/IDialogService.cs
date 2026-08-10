namespace OrderDeck.App.Services;

/// <summary>Bildirimin tonu. WPF'te MessageBoxImage'a eşlenir.</summary>
public enum DialogSeverity
{
    Info,
    Warning,
    Error,
}

public interface IDialogService
{
    /// <summary>Telefon giriş çekmecesini açar; kaydedildiyse true. Faz 3'te
    /// modal pencereden çekmeceye geçti, bu yüzden async — iki çağrı yeri de
    /// zaten async bir komutun içindeydi.</summary>
    Task<bool> ShowPhoneEntryAsync(string customerId);

    void ShowError(string message);

    /// <summary>Hata OLMAYAN bilgilendirme. <see cref="ShowError"/> başlığı ve
    /// ikonu "Hata" olarak sabit; belirsiz bir durumu (ör. gönderim işleniyor,
    /// sonuç bilinmiyor) onunla göstermek operatöre yanlış bilgi verir.</summary>
    void ShowInfo(string message);

    /// <summary>Başlığı çağıran belirleyen bildirim. <see cref="ShowInfo"/> ve
    /// <see cref="ShowError"/> başlığı sabitler; kabuk her uyarıyı kendi
    /// bağlamıyla ("Yayın aktif", "Geçersiz fiyat", ...) adlandırıyor.</summary>
    void Show(string message, string title, DialogSeverity severity = DialogSeverity.Info);

    /// <summary>Evet/Hayır onayı; Evet ise true. Sahte uygulama varsayılan
    /// olarak false döner — testte yanlışlıkla yıkıcı bir dala girilmesin.</summary>
    bool Confirm(string message, string title);

    // ── Çekmece karşılıkları (Faz 2) ────────────────────────────────────
    // Yukarıdaki beş metot MessageBox açıyor; spec §6 "hiçbir şey pop-up
    // değil" diyor. Aşağıdakiler aynı işi kabuğun içindeki çekmecede yapar.
    // İkisi bir süre YAN YANA duracak: 26 çağrı yerini tek seferde çevirmek
    // ViewModel'lerin akışını da async'e taşımak demek, o Faz 2b'nin işi.
    // Dönüşüm bitince senkron beşli silinecek.

    /// <summary>Evet/Vazgeç onayı, çekmecede. <see cref="Confirm"/> ile aynı
    /// sözleşme: Evet ise true, ESC/Vazgeç ise false.</summary>
    Task<bool> ConfirmAsync(string message, string title);

    /// <summary>Tek düğmeli bildirim, çekmecede. <see cref="Show"/>'un
    /// karşılığı; önem şeridin rengine dönüşür.</summary>
    Task ShowAsync(string message, string title, DialogSeverity severity = DialogSeverity.Info);
}
