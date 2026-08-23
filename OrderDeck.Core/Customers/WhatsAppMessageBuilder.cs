using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OrderDeck.Core.Customers;

/// <summary>
/// Phase 4g: Settings template'ini PaymentContext ile substitute eder
/// ve wa.me deep-link inşa eder. TR culture decimal/tarih formatlama.
/// </summary>
public sealed class WhatsAppMessageBuilder
{
    private static readonly CultureInfo Tr = new("tr-TR");

    public string BuildMessage(string template, PaymentContext ctx)
    {
        // Kargo placeholder'ları (2026-05-12): {urun_toplami}, {kargo_ucreti},
        // {kargo}. Eski template'ler bu placeholder'ları içermez — sessiz geçer.
        //
        // E3b bakiye placeholder'ları (2026-06):
        //   {bakiye}      — bu mesajda düşülen bakiye (örn. "100,00")
        //                   AppliedBalance == 0 ise boş string
        //   {net_tutar}   — bakiye düşüldükten sonra ödenecek tutar (== TotalAmount)
        //   {toplam_oncesi}— bakiye düşülmeden önceki toplam (TotalBeforeBalance)
        // Bu placeholder'lar geriye uyumlu — eski template'lerde yer almıyorsa
        // sessiz geçer.
        var values = WhatsAppFieldCatalog.BuildValues(ctx);
        var result = template;
        foreach (var field in WhatsAppFieldCatalog.All)
        {
            result = result.Replace("{" + field.Key + "}", values[field.Key]);
        }

        return result;
    }

    /// <summary>
    /// Onaylı Meta şablonunun gövde parametrelerini üretir.
    ///
    /// <para><paramref name="fieldKeys"/> yayıncının ayar ekranında kurduğu
    /// eşleme: <c>{{1}}</c> hangi alan, <c>{{2}}</c> hangi alan… Sıra ŞABLONA
    /// bağlı ve eşlemeyi yayıncı kurar, çünkü şablonun <b>adı ve dili Meta'da
    /// kilitli</b> — onaya girmiş bir şablon bizim beklediğimiz şekle
    /// getirilemez, uyum sağlaması gereken taraf biziz. (Bu metot uzun süre
    /// sabit yedi değerlik bir dizi döndürüyordu; sahadaki dört parametreli
    /// onaylı şablonla hiç tutmadı ve her gönderim sessizce <c>wa.me</c>'ye
    /// düştü.)</para>
    ///
    /// <para><b>Eksik değer varsa null döner.</b> Meta boş parametreyi kabul
    /// etmiyor; IBAN'ı ya da hesap sahibi girilmemiş bir yayıncıda şablonu
    /// denemek her gönderimde hata almak demek. null = "şablon yolu yok",
    /// çağıran eski <c>wa.me</c> davranışına düşer. Eşlemenin kendisi boşsa ya
    /// da tanınmayan bir alan içeriyorsa da null döner — aynı güvenli düşüş.</para>
    ///
    /// <para>Meşru olarak boş kalabilen alanlar (kargo notu, bakiye) iptal
    /// sebebi değil, tire ile gider; bkz.
    /// <see cref="WhatsAppField.EmptyIsLegitimate"/>.</para>
    /// </summary>
    public IReadOnlyList<string>? BuildPaymentTemplateParams(
        PaymentContext ctx, IReadOnlyList<string> fieldKeys)
    {
        if (fieldKeys is null || fieldKeys.Count == 0) return null;

        var values = WhatsAppFieldCatalog.BuildValues(ctx);
        var result = new string[fieldKeys.Count];
        for (var i = 0; i < fieldKeys.Count; i++)
        {
            var field = WhatsAppFieldCatalog.TryFind(fieldKeys[i] ?? "");
            if (field is null) return null;

            var value = Sanitize(values[field.Key]);
            if (value.Length == 0)
            {
                if (!field.EmptyIsLegitimate) return null;
                value = WhatsAppFieldCatalog.EmptyPlaceholder;
            }

            result[i] = value;
        }

        return result;
    }

    /// <summary>Şablon parametresi satır sonu, sekme ya da 4'ten fazla ardışık
    /// boşluk taşıyamaz — Meta isteği reddeder. Değerlerin bir kısmı sohbetten
    /// gelen kullanıcı adı olduğu için bunu varsayamayız.</summary>
    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var sb = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace && sb.Length > 0) sb.Append(' ');
            pendingSpace = false;
            sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Kümülatif kargo PR-E (2026-05-12): ücretsiz kargo eşiği aşıldı
    /// "tebrikler" şablonu için placeholder substitusyonu.
    /// Placeholder: {ad}, {kumulatif_tutar}, {tarih}.
    /// </summary>
    public string BuildShippingWonMessage(string template, string displayName, decimal cumulativeAmount)
    {
        return template
            .Replace("{ad}", displayName)
            .Replace("{kumulatif_tutar}", cumulativeAmount.ToString("N2", Tr))
            .Replace("{tarih}", DateTime.Now.ToString("dd MMMM yyyy", Tr));
    }

    /// <summary>"+905551234567" + "Hello" → "https://wa.me/905551234567?text=Hello".</summary>
    public string BuildWaMeLink(string e164Phone, string message)
    {
        var phone = e164Phone.TrimStart('+');
        return $"https://wa.me/{phone}?text={Uri.EscapeDataString(message)}";
    }
}
