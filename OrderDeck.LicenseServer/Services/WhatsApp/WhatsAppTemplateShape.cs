using System.Globalization;
using System.Text.RegularExpressions;

namespace OrderDeck.LicenseServer.Services.WhatsApp;

/// <summary>Şablon butonu. <paramref name="Type"/> yalnız <c>QUICK_REPLY</c>,
/// <c>URL</c> ya da <c>PHONE_NUMBER</c> olabilir — gönderenimiz buton parametresi
/// yollamadığı için ötekiler oluşturulur ama gönderilemezdi.</summary>
public sealed record WhatsAppTemplateButton(string Type, string Text, string? Url, string? PhoneNumber);

/// <summary>
/// Şablonun <b>bileşenleri</b> — ad, kategori ve dil bilerek yok.
///
/// <para>Meta'nın düzenleme ucu yalnız bileşenleri güncelliyor; ad/kategori/dil
/// buraya konsaydı güncelleme yolunda uydurma değer taşımak gerekirdi.</para>
/// </summary>
public sealed record WhatsAppTemplateDraft(
    string? HeaderText,
    string BodyText,
    string? FooterText,
    IReadOnlyList<string> BodyExamples,
    IReadOnlyList<WhatsAppTemplateButton> Buttons);

/// <summary>
/// Şablon gövdesindeki yer tutucuların çözümlenmesi.
///
/// <para>HTTP'den ayrı duruyor çünkü asıl incelik burada: yanlış sayıda parametre
/// göndermek Meta'dan 132000 ile döner ve şablon <b>ücretli</b> olduğu için
/// yayıncı parasını denemelere yatırır.</para>
/// </summary>
public static class WhatsAppTemplateShape
{
    /// <summary>Yer tutucunun kendisi — içi rakam mı isim mi, ayrımı çağıran yapar.</summary>
    private static readonly Regex Placeholder =
        new(@"\{\{(.*?)\}\}", RegexOptions.CultureInvariant);

    public const string NamedParams =
        "Bu şablon isimli değişken kullanıyor; panel yalnız {{1}}, {{2}} biçimini gönderebiliyor.";

    public const string GappedParams =
        "Şablonun değişken numaraları 1'den başlayarak sırayla gitmiyor.";

    public const string HeaderMedia =
        "Şablonun başlığında görsel/belge var; panel yalnız metin başlıklı şablon gönderebiliyor.";

    public const string HeaderVariable =
        "Şablonun başlığında değişken var; panel yalnız gövde değişkenlerini doldurabiliyor.";

    public const string FooterVariable =
        "Şablonun alt bilgisinde değişken kullanılamıyor; Meta bunu reddediyor.";

    public const string ButtonVariable =
        "Şablonun butonu değişken istiyor; panel bu tür şablonu gönderemiyor.";

    public const string ButtonTypeUnsupported =
        "Panel yalnız hızlı yanıt, bağlantı ve arama butonu gönderebiliyor.";

    public const string AuthCategory =
        "Doğrulama (authentication) şablonları ayrı bir gönderim biçimi istiyor.";

    /// <summary>
    /// Gövdedeki konumsal parametre sayısı.
    /// </summary>
    /// <returns><c>Unsupported</c> doluysa gövde bizim gönderebileceğimiz
    /// biçimde değil ve <c>Count</c> anlamsızdır.</returns>
    public static (int Count, string? Unsupported) CountBodyParams(string bodyText)
    {
        var matches = Placeholder.Matches(bodyText);
        if (matches.Count == 0) return (0, null);

        var indexes = new SortedSet<int>();
        foreach (Match m in matches)
        {
            var inner = m.Groups[1].Value.Trim();
            // Meta 2024'ten beri {{musteri_adi}} gibi isimli değişkene de izin
            // veriyor. Gönderenimiz konumsal dizi yolluyor; isimli şablonda o
            // dizi sessizce yanlış yere oturmaz, Meta reddeder — ama ücretli
            // denemeye bırakmak yerine burada eliyoruz.
            if (!int.TryParse(inner, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var n) || n < 1)
            {
                return (0, NamedParams);
            }
            indexes.Add(n);
        }

        // 1..n bitişik olmalı. Meta bunu kendi de zorluyor ama liste bizim
        // verimiz değil: boşluklu bir dizide ({{1}}, {{3}}) yayıncının girdiği
        // değerler bir sıra kayar ve yanlış bilgi müşteriye gider.
        var expected = 1;
        foreach (var n in indexes)
        {
            if (n != expected++) return (0, GappedParams);
        }

        return (indexes.Count, null);
    }

    private static readonly Regex NamePattern =
        new("^[a-z0-9_]+$", RegexOptions.CultureInvariant);

    private const int MaxNameLength = 512;
    private const int MaxBodyLength = 1024;
    private const int MaxHeaderLength = 60;
    private const int MaxFooterLength = 60;
    private const int MaxButtonTextLength = 25;
    private const int MaxButtons = 10;
    private const int MaxUrlButtons = 2;
    private const int MaxPhoneButtons = 1;

    /// <returns>İlk hata metni (Türkçe, doğrudan yayıncıya gösterilir) ya da null.</returns>
    public static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Şablon adı boş olamaz.";
        if (name.Length > MaxNameLength) return $"Şablon adı en çok {MaxNameLength} karakter olabilir.";
        if (!NamePattern.IsMatch(name))
            return "Şablon adı yalnız küçük harf, rakam ve alt çizgi içerebilir (örn. siparis_hatirlatma).";
        return null;
    }

    /// <summary>Yalnız iki kategori. <c>AUTHENTICATION</c> OTP buton parametresi
    /// ister; gönderenimiz onu yollamıyor, yani oluşturulur ama gönderilemezdi.</summary>
    public static string? ValidateCategory(string category) =>
        category is "MARKETING" or "UTILITY"
            ? null
            : "Kategori yalnız MARKETING ya da UTILITY olabilir.";

    /// <summary>Bileşen doğrulaması. Meta'ya çıkmadan eliyoruz: 132000 hatası
    /// okunmaz ve şablon ücretli.</summary>
    public static string? Validate(WhatsAppTemplateDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.BodyText)) return "Mesaj metni boş olamaz.";
        if (draft.BodyText.Length > MaxBodyLength)
            return $"Mesaj metni en çok {MaxBodyLength} karakter olabilir.";

        var (count, unsupported) = CountBodyParams(draft.BodyText);
        if (unsupported is not null) return unsupported;

        if (draft.BodyExamples.Count != count)
            return $"Metinde {count} değişken var; {count} örnek değer girilmeli.";
        if (draft.BodyExamples.Any(string.IsNullOrWhiteSpace))
            return "Örnek değerler boş bırakılamaz; Meta örneksiz şablonu reddediyor.";

        if (draft.HeaderText is { } header)
        {
            if (header.Length > MaxHeaderLength)
                return $"Başlık en çok {MaxHeaderLength} karakter olabilir.";
            if (header.Contains("{{", StringComparison.Ordinal)) return HeaderVariable;
        }

        if (draft.FooterText is { } footer)
        {
            if (footer.Length > MaxFooterLength)
                return $"Alt bilgi en çok {MaxFooterLength} karakter olabilir.";
            if (footer.Contains("{{", StringComparison.Ordinal)) return FooterVariable;
        }

        return ValidateButtons(draft.Buttons);
    }

    private static string? ValidateButtons(IReadOnlyList<WhatsAppTemplateButton> buttons)
    {
        if (buttons.Count == 0) return null;
        if (buttons.Count > MaxButtons) return $"En çok {MaxButtons} buton eklenebilir.";

        var urls = 0;
        var phones = 0;

        foreach (var b in buttons)
        {
            if (string.IsNullOrWhiteSpace(b.Text)) return "Buton yazısı boş olamaz.";
            if (b.Text.Length > MaxButtonTextLength)
                return $"Buton yazısı en çok {MaxButtonTextLength} karakter olabilir.";

            switch (b.Type)
            {
                case "QUICK_REPLY":
                    break;

                case "URL":
                    if (string.IsNullOrWhiteSpace(b.Url)) return "Bağlantı butonunda adres boş olamaz.";
                    if (b.Url.Contains("{{", StringComparison.Ordinal)) return ButtonVariable;
                    if (++urls > MaxUrlButtons)
                        return $"En çok {MaxUrlButtons} bağlantı butonu eklenebilir.";
                    break;

                case "PHONE_NUMBER":
                    if (string.IsNullOrWhiteSpace(b.PhoneNumber)) return "Arama butonunda numara boş olamaz.";
                    if (++phones > MaxPhoneButtons)
                        return $"En çok {MaxPhoneButtons} arama butonu eklenebilir.";
                    break;

                default:
                    return ButtonTypeUnsupported;
            }
        }

        // Meta hızlı yanıt butonlarının bitişik durmasını şart koşuyor. Sessizce
        // yeniden sıralamak yayıncının tasarladığı düzeni değiştirmek olurdu.
        var seenOther = false;
        var reopened = false;
        var started = false;
        foreach (var b in buttons)
        {
            if (b.Type == "QUICK_REPLY")
            {
                if (started && seenOther) reopened = true;
                started = true;
            }
            else if (started)
            {
                seenOther = true;
            }
        }
        if (reopened) return "Hızlı yanıt butonları arka arkaya sıralanmalı.";

        return null;
    }
}
