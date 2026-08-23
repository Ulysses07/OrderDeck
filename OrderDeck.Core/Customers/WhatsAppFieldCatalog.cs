using System;
using System.Collections.Generic;
using System.Globalization;

namespace OrderDeck.Core.Customers;

/// <summary>
/// Ödeme mesajına yazılabilen tek bir alan.
/// </summary>
/// <param name="Key">Serbest metin kalıbındaki yer tutucu adı
/// (<c>{ad}</c> → <c>"ad"</c>) ve ayarda saklanan eşleme anahtarı. Bu iki
/// kullanımın aynı anahtarı paylaşması bilinçli: yayıncı ayar ekranında
/// gördüğü adı kalıpta da arayabiliyor.</param>
/// <param name="Label">Ayar ekranındaki açılır listede görünen Türkçe ad.</param>
/// <param name="EmptyIsLegitimate">
/// Değerin boş gelmesi eksik yapılandırma DEĞİL, normal bir durum mu?
///
/// <para>Şablon yolunda önemli: Meta boş parametre kabul etmiyor, bu yüzden boş
/// bir değer normalde şablonu tamamen iptal ettirir (çağıran <c>wa.me</c>'ye
/// düşer). Ama kargo notu, kargo özelliği kapalıyken; bakiye de müşterinin
/// bakiyesi yokken meşru olarak boş geliyor. Bunları da iptal sebebi saymak,
/// bu alanları eşleyen yayıncıda şablon yolunu fiilen hiç çalıştırmazdı —
/// onların yerine tire konuyor. IBAN'ın boş olması ise gerçekten eksik
/// yapılandırma; orada şablonu göndermemek doğru davranış.</para>
/// </param>
public sealed record WhatsAppField(string Key, string Label, bool EmptyIsLegitimate = false);

/// <summary>
/// Ödeme mesajının alan sözlüğü — hem serbest metin kalıbının yer tutucuları
/// hem onaylı Meta şablonunun gövde parametreleri buradan üretilir.
///
/// <para><b>Neden tek kaynak:</b> iki yol ayrı listeler tuttuğunda birine
/// eklenen alan öbüründe eksik kalıyor ve fark ancak müşteriye yanlış mesaj
/// gidince görülüyor. Nitekim şablon tarafı uzun süre sabit yedi değerlik bir
/// diziydi ve yayıncının Meta'da onaylattığı dört parametreli şablonla hiç
/// tutmadı: her gönderim sessizce <c>wa.me</c>'ye düştü.</para>
/// </summary>
public static class WhatsAppFieldCatalog
{
    private static readonly CultureInfo Tr = new("tr-TR");

    /// <summary>Meşru olarak boş kalan alanların şablondaki karşılığı.</summary>
    public const string EmptyPlaceholder = "—";

    /// <summary>
    /// Sıra anlamlı: yer tutucu ikamesi bu sırayla yapılıyor ve bir değerin
    /// içinde başka bir yer tutucuya benzeyen metin geçtiğinde (ör. müşteri adı
    /// olarak yazılmış <c>{iban}</c>) sonucu sıra belirliyor. Mevcut davranışla
    /// birebir aynı kalsın diye eski ikame zincirinin sırası korundu.
    /// </summary>
    public static IReadOnlyList<WhatsAppField> All { get; } = new[]
    {
        new WhatsAppField("ad", "Müşteri adı"),
        new WhatsAppField("tutar", "Ödenecek tutar"),
        new WhatsAppField("tarih", "Yayın tarihi"),
        new WhatsAppField("iban", "IBAN"),
        new WhatsAppField("hesap_sahibi", "Hesap sahibi"),
        new WhatsAppField("papara", "Papara"),
        new WhatsAppField("urun_toplami", "Ürün toplamı"),
        new WhatsAppField("kargo_ucreti", "Kargo ücreti"),
        new WhatsAppField("kargo", "Kargo notu", EmptyIsLegitimate: true),
        new WhatsAppField("bakiye", "Düşülen bakiye", EmptyIsLegitimate: true),
        new WhatsAppField("net_tutar", "Bakiye sonrası tutar"),
        new WhatsAppField("toplam_oncesi", "Bakiye öncesi toplam"),
    };

    public static bool IsKnownKey(string? key) =>
        key is not null && TryFind(key) is not null;

    public static WhatsAppField? TryFind(string key)
    {
        foreach (var f in All)
        {
            if (string.Equals(f.Key, key, StringComparison.Ordinal)) return f;
        }

        return null;
    }

    /// <summary>
    /// Alan adı → değer. Değerler HAM: serbest metin kalıbının bugünkü
    /// davranışıyla birebir aynı (boş kalan alan boş string). Şablon yolunun
    /// istediği tire ikamesi ve temizleme, oraya özgü olduğu için
    /// <see cref="WhatsAppMessageBuilder.BuildPaymentTemplateParams"/>'ta yapılır.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildValues(PaymentContext ctx)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ad"] = ctx.DisplayName ?? "",
            ["tutar"] = ctx.TotalAmount.ToString("N2", Tr),
            ["tarih"] = ctx.StreamDate.ToString("dd MMMM yyyy", Tr),
            ["iban"] = ctx.Iban ?? "",
            ["hesap_sahibi"] = ctx.AccountHolder ?? "",
            ["papara"] = ctx.Papara ?? "",
            ["urun_toplami"] = ctx.ProductTotal.ToString("N2", Tr),
            ["kargo_ucreti"] = ctx.ShippingFee.HasValue
                ? ctx.ShippingFee.Value.ToString("N2", Tr)
                : EmptyPlaceholder,
            ["kargo"] = ctx.ShippingNote ?? "",
            ["bakiye"] = ctx.AppliedBalance > 0 ? ctx.AppliedBalance.ToString("N2", Tr) : "",
            ["net_tutar"] = ctx.TotalAmount.ToString("N2", Tr),
            ["toplam_oncesi"] =
                (ctx.TotalBeforeBalance > 0 ? ctx.TotalBeforeBalance : ctx.TotalAmount)
                .ToString("N2", Tr),
        };
    }
}
