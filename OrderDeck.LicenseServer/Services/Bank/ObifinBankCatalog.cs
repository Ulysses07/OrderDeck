using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>Obifin <c>bankaapi/ekle</c> formunun bir alanı. <paramref name="Key"/> Obifin'in beklediği ad (harfi harfine,
/// ör. <c>associationCode</c>); <paramref name="Label"/> admin'in gördüğü Türkçe ad; <paramref name="Secret"/> = parola
/// kutusu (ekran paylaşımında/omuz üstünden okunmasın); <paramref name="Required"/> = Obifin'in <c>ZorunluAlanlar</c>'ında.</summary>
public sealed record ObifinBankField(string Key, string Label, bool Secret, bool Required, string? Hint = null);

/// <summary>Obifin'in bir banka türü (<c>BankaKodu</c>). <paramref name="Method"/> <see cref="ObifinBankCatalog.WebService"/>
/// ya da <see cref="ObifinBankCatalog.Api"/>; <paramref name="Fields"/> önce zorunlular, sonra isteğe bağlılar, en sonda VKN.</summary>
public sealed record ObifinBankType(string Code, string DisplayName, string Method, IReadOnlyList<ObifinBankField> Fields);

/// <summary>Obifin'in desteklediği 35 banka türü ve her birinin <c>bankaapi/ekle</c> alanları. Kodlar ve zorunlu alanlar
/// Obifin'in kendi <c>bankaapi/bankakodlari/</c> yanıtından (test API'si, 2026-09-28) ölçüldü; isteğe bağlı alanlar ve
/// bankaya özgü açıklamalar Obifin Postman koleksiyonu v1.03.06'dan. Tablo testle sabitlenmiştir
/// (<c>ObifinBankCatalogTests</c>): Obifin bir bankanın alanlarını değiştirirse ikisi birlikte güncellenir.
/// <para><c>BankaApiAdi</c> hiçbir türde yok: Obifin tarafındaki etiketi <see cref="ObifinConnectionService.AddBankConnectionAsync"/>
/// kendisi üretir. Katalog yalnız formun şeklidir — değer taşımaz, saklamaz.</para></summary>
public static class ObifinBankCatalog
{
    /// <summary>Bankanın hesap hareketleri web servisi (kurumsal internet şubesi / şube başvurusu).</summary>
    public const string WebService = "Web servis";
    /// <summary>Bankanın API başvurusu. Obifin'de kodu <c>…api</c> ile biten dört tür.</summary>
    public const string Api = "API";

    /// <summary>Obifin anahtarı → Türkçe etiket. Etiket önde, onu kullanan anahtarlar arkada: birden çok anahtar aynı adı
    /// paylaşır (Obifin bazı bankalarda İngilizce ad ister).</summary>
    private static readonly (string Etiket, string[] Anahtarlar)[] OrtakEtiketler =
    [
        ("Kullanıcı adı", ["KullaniciAdi", "Username"]),
        ("Şifre", ["Sifre", "Password"]),
        ("Servis adresi (WSDL)", ["Url"]),
        ("Firma kodu", ["FirmaKodu"]),
        ("Firma anahtarı", ["FirmaAnahtar"]),
        ("Firma adı", ["FirmaAdi"]),
        ("Kurum kodu", ["KurumKodu", "CorporationCode"]),
        ("Kurum kodu (associationCode)", ["associationCode"]),
        ("Kurum kullanıcısı", ["KurumKullanici"]),
        ("Kullanıcı kodu", ["KullaniciKodu"]),
        ("Müşteri numarası", ["MusteriNo"]),
        ("Anahtar (Key)", ["Key"]),
        ("Client ID", ["ClientId"]),
        ("Client secret", ["ClientSecret"]),
        ("Erişim token'ı", ["AccessToken"]),
        ("Yenileme token'ı", ["RefreshToken"]),
        ("Tanım numarası", ["TanimNumarasi"]),
        ("API anahtarı", ["APIKey"]),
        ("API gizli anahtarı", ["APISecret"]),
        ("Servis ID", ["ServiceID"]),
        ("Ekstre firma ID", ["StatementCompanyId"]),
    ];

    private static readonly Dictionary<string, string> Etiketler = OrtakEtiketler
        .SelectMany(e => e.Anahtarlar.Select(a => (Anahtar: a, e.Etiket)))
        .ToDictionary(x => x.Anahtar, x => x.Etiket, StringComparer.Ordinal);

    /// <summary>Parola kutusuna düşen anahtarlar.</summary>
    private static readonly HashSet<string> Maskeli = new(StringComparer.Ordinal)
        { "Sifre", "Password", "ClientSecret", "AccessToken", "RefreshToken", "APIKey", "APISecret", "FirmaAnahtar", "Key" };

    private const string IstegeBagliEki = " (isteğe bağlı)";

    /// <summary>Her banka türü kabul eder, hiçbirinde zorunlu değil (Postman v1.03.06): hesap sahibi şirketin vergi
    /// numarası, muhasebe için.</summary>
    private static readonly ObifinBankField Vkn = new("VKN", "Şirket VKN (isteğe bağlı)", Secret: false, Required: false,
        Hint: "Hesap sahibi şirketin vergi kimlik numarası (muhasebe için).");

    /// <summary>Bankaya özgü etiket ya da ipucu (Postman koleksiyonundaki alan açıklamaları).</summary>
    private sealed record Ozel(string Anahtar, string? Etiket = null, string? Ipucu = null);

    private static readonly Ozel WsdlIpucu = new("Url", Ipucu: "Bankanın verdiği web servis adresi (WSDL).");

    private static readonly ObifinBankType[] Tanimlar =
    [
        Tur("akbank", "Akbank", ["FirmaAnahtar", "KullaniciAdi", "Sifre"]),
        Tur("aktifbank", "Aktif Yatırım Bankası", ["KullaniciAdi", "Sifre"]),
        Tur("alternatifbank", "Alternatif Bank", ["KurumKodu", "Sifre"]),
        Tur("albarakaturk", "Albaraka Türk Katılım", ["KullaniciAdi", "Sifre"]),
        Tur("anadolubank", "Anadolubank", ["MusteriNo", "Key"],
            istegeBagli: ["KullaniciAdi", "Sifre", "ClientId", "ClientSecret"],
            istegeBagliIpucu: "Tercihe bağlı; boş ise Obifin varsayılanı işlenir."),
        Tur("burganbank", "Burgan Bank", ["KullaniciAdi", "Sifre"]),
        Tur("denizbank", "DenizBank", ["FirmaAnahtar", "KullaniciAdi"],
            ozel: [new("FirmaAnahtar", Etiket: "Firma anahtarı (AppKey)"), new("KullaniciAdi", Etiket: "Uygulama kodu")]),
        Tur("dunyakatilim", "Dünya Katılım", ["StatementCompanyId", "Username", "Password"]),
        Tur("emlakbank", "Türkiye Emlak Katılım", ["KullaniciAdi", "Sifre", "MusteriNo"]),
        Tur("enpara", "Enpara — web servis", ["KullaniciAdi", "Sifre", "Url"], ozel: [WsdlIpucu]),
        Tur("enparaapi", "Enpara — API", ["ClientId", "ClientSecret", "AccessToken", "RefreshToken"]),
        Tur("fibabanka", "Fibabanka", ["FirmaKodu", "Sifre"]),
        Tur("garanti", "Garanti BBVA — web servis", ["KullaniciAdi", "Sifre", "FirmaKodu"]),
        Tur("garantibbvaapi", "Garanti BBVA — API", ["TanimNumarasi"]),
        Tur("halkbank", "Halkbank", ["KullaniciAdi", "Sifre"]),
        Tur("hsbc", "HSBC", ["associationCode", "Username", "Password"]),
        Tur("icbc", "ICBC", ["MusteriNo", "Sifre"]),
        Tur("ingbank", "ING Bank", ["MusteriNo", "KullaniciAdi", "Sifre"]),
        Tur("isbank", "İş Bankası", ["KullaniciAdi", "Sifre"]),
        Tur("kuveytturk", "Kuveyt Türk — web servis", ["KullaniciAdi", "Sifre"]),
        Tur("kuveytturkapi", "Kuveyt Türk — API", []),
        Tur("odeabank", "Odeabank", ["KullaniciKodu", "Sifre", "MusteriNo"]),
        Tur("papara", "Papara", ["APIKey", "APISecret"]),
        Tur("turkland", "Turkland Bank", ["KullaniciAdi", "Sifre"],
            ozel: [new("KullaniciAdi", Etiket: "Kullanıcı adı (p1)"), new("Sifre", Etiket: "Şifre (seckod)")]),
        Tur("turkticaret", "Türk Ticaret Bankası", ["CorporationCode", "APIKey", "Password", "ClientId", "ClientSecret"]),
        Tur("qnb", "QNB — web servis", ["KullaniciAdi", "Sifre", "Url"], ozel: [WsdlIpucu]),
        Tur("qnbapi", "QNB — API", ["ClientId", "ClientSecret", "AccessToken", "RefreshToken"]),
        Tur("sekerbank", "Şekerbank", ["KullaniciAdi", "Sifre"]),
        Tur("teb", "TEB (Türk Ekonomi Bankası)", ["FirmaAdi", "FirmaAnahtar"],
            istegeBagli: ["KullaniciAdi", "Sifre", "ServiceID"],
            istegeBagliIpucu: "Banka size iletmediyse boş bırakın; boş ise varsayılan değer işlenir."),
        Tur("turkiyefinans", "Türkiye Finans Katılım", ["KullaniciAdi", "Sifre"]),
        Tur("vakifbank", "VakıfBank", ["KurumKullanici", "Sifre", "MusteriNo"]),
        Tur("vakifkatilim", "Vakıf Katılım", ["KullaniciAdi", "Sifre", "MusteriNo"]),
        Tur("yapikredi", "Yapı Kredi", ["FirmaKodu", "KullaniciAdi", "Sifre"]),
        Tur("ziraat", "Ziraat Bankası", ["KurumKodu", "Sifre", "MusteriNo"],
            ozel: [new("KurumKodu", Ipucu: "Ziraat'in paylaştığı kurum kodu"), new("MusteriNo", Ipucu: "Ziraat'teki müşteri numarası")]),
        Tur("ziraatkatilim", "Ziraat Katılım", ["FirmaKodu", "KullaniciAdi", "Sifre"]),
    ];

    /// <summary>Görünen ada göre, Türkçe alfabe sırasıyla (İ, Ş kod noktasına göre en sona düşmez).</summary>
    public static IReadOnlyList<ObifinBankType> All { get; } = Tanimlar
        .OrderBy(t => t.DisplayName, StringComparer.Create(CultureInfo.GetCultureInfo("tr-TR"), ignoreCase: false))
        .ToList().AsReadOnly();

    /// <summary>Kod tekrarı burada, tip yüklenirken patlar.</summary>
    private static readonly Dictionary<string, ObifinBankType> KodaGore =
        Tanimlar.ToDictionary(t => t.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>Kodu harf duyarsız (ve baştaki/sondaki boşluğu yok sayarak) arar; bulunursa kanonik (küçük harf) kodlu türü döner.</summary>
    public static bool TryGet(string? code, [MaybeNullWhen(false)] out ObifinBankType type)
    {
        if (code is null)
        {
            type = null;
            return false;
        }
        return KodaGore.TryGetValue(code.Trim(), out type);
    }

    /// <summary>Alan sırası: <paramref name="zorunlu"/> (Obifin'in sırasıyla), <paramref name="istegeBagli"/> (etikete
    /// "(isteğe bağlı)" eklenir), en sonda VKN. Yöntem koddan: <c>…api</c> = API, gerisi web servis.</summary>
    private static ObifinBankType Tur(string kod, string ad, string[] zorunlu, Ozel[]? ozel = null,
        string[]? istegeBagli = null, string? istegeBagliIpucu = null)
    {
        var ozelMap = (ozel ?? []).ToDictionary(o => o.Anahtar, StringComparer.Ordinal);
        ObifinBankField Alan(string anahtar, bool zorunluMu, string? ipucu)
        {
            ozelMap.TryGetValue(anahtar, out var o);
            var etiket = o?.Etiket ?? Etiketler[anahtar];
            return new ObifinBankField(anahtar, zorunluMu ? etiket : etiket + IstegeBagliEki, Maskeli.Contains(anahtar),
                zorunluMu, o?.Ipucu ?? ipucu);
        }
        var fields = zorunlu.Select(a => Alan(a, true, null))
            .Concat((istegeBagli ?? []).Select(a => Alan(a, false, istegeBagliIpucu)))
            .Append(Vkn)
            .ToList().AsReadOnly();
        return new ObifinBankType(kod, ad, kod.EndsWith("api", StringComparison.Ordinal) ? Api : WebService, fields);
    }
}
