namespace OrderDeck.App.Services;

public interface IDialogService
{
    bool ShowPhoneEntryDialog(string customerId);
    void ShowError(string message);

    /// <summary>Hata OLMAYAN bilgilendirme. <see cref="ShowError"/> başlığı ve
    /// ikonu "Hata" olarak sabit; belirsiz bir durumu (ör. gönderim işleniyor,
    /// sonuç bilinmiyor) onunla göstermek operatöre yanlış bilgi verir.</summary>
    void ShowInfo(string message);
}
