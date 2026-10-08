namespace OrderDeck.Core.Customers;

/// <summary>
/// Bir müşterinin senkronlanan alanları ve her BİRİMİN damgası (unix MS) —
/// sunucudaki <c>CustomerSyncFields</c>'ın yerel aynası. Birim = tek alan ya da
/// blok: adres (Address+City+District) ve kara liste (IsBlacklisted+BlacklistReason+
/// BlacklistedAt). <see cref="BlacklistedAt"/> yerelde unix SANİYE (Customer
/// tablosuyla aynı). <see cref="Username"/> yalnız takma ad kuralı içindir;
/// <see cref="PurgedAt"/> (unix s) yerel silme kapısıdır — doluysa hiçbir birim yazılmaz.
/// </summary>
public sealed class CustomerSyncState
{
    public string Username { get; set; } = "";
    public long? PurgedAt { get; set; }

    public string? FullName { get; set; }
    public long? FullNameChangedAt { get; set; }
    public string? DisplayName { get; set; }
    public long? DisplayNameChangedAt { get; set; }
    public string? GroupId { get; set; }
    public long? GroupIdChangedAt { get; set; }

    public string? Address { get; set; }
    public string? City { get; set; }
    public string? District { get; set; }
    public long? AddressChangedAt { get; set; }
    public bool RecipientPaysActive { get; set; }
    public long? RecipientPaysChangedAt { get; set; }

    public string? Phone { get; set; }
    public long? PhoneChangedAt { get; set; }
    public string? Email { get; set; }
    public long? EmailChangedAt { get; set; }
    /// <summary>DÜZ metin (sunucu şifreli tutar, akış çözüp gönderir).</summary>
    public string? Tckn { get; set; }
    public long? TcknChangedAt { get; set; }
    /// <summary>Yalnız gösterim/senkron — SMS/WhatsApp gönderiminin dayanağı DEĞİLDİR
    /// (sunucuda Shopper.SmsConsent + İYS aynası yetkili).</summary>
    public bool WhatsAppConsent { get; set; }
    public long? WhatsAppConsentChangedAt { get; set; }
    public bool SmsConsent { get; set; }
    public long? SmsConsentChangedAt { get; set; }

    public bool IsBlacklisted { get; set; }
    public string? BlacklistReason { get; set; }
    public long? BlacklistedAt { get; set; }
    public long? BlacklistChangedAt { get; set; }

    public string? Notes { get; set; }
    public long? NotesChangedAt { get; set; }

    public CustomerSyncState Clone() => (CustomerSyncState)MemberwiseClone();

    /// <summary>Sunucudaki <c>StampedOnly()</c>'nin aynası: yalnız DAMGALI birimler
    /// (bir bilgisayarda bilerek yapılmış kararlar) kalır; damgasız birim boşalır,
    /// damgasız bayrak false olur. Sunucunun devralmada geçici satırdan taşıdığı küme
    /// budur (S8). İstemcinin yeniden anahtarlamada kopyadan taşıdığı küme bundan geniştir
    /// (<see cref="WithoutShopperClaims"/>, U3a): beyan olamayan birimlerin damgasız
    /// değerleri de doldurma olarak taşınır — geçici kökenli kopyanın gönderimini sunucu
    /// hiç yazmadığı için (S7) yoksa kaybolurlardı.</summary>
    public CustomerSyncState StampedOnly()
    {
        var s = Clone();
        if (s.FullNameChangedAt is null) s.FullName = null;
        if (s.DisplayNameChangedAt is null) s.DisplayName = null;
        if (s.GroupIdChangedAt is null) s.GroupId = null;
        if (s.AddressChangedAt is null) { s.Address = null; s.City = null; s.District = null; }
        s.RecipientPaysActive = s.RecipientPaysChangedAt is not null && s.RecipientPaysActive;
        if (s.PhoneChangedAt is null) s.Phone = null;
        if (s.EmailChangedAt is null) s.Email = null;
        if (s.TcknChangedAt is null) s.Tckn = null;
        s.WhatsAppConsent = s.WhatsAppConsentChangedAt is not null && s.WhatsAppConsent;
        s.SmsConsent = s.SmsConsentChangedAt is not null && s.SmsConsent;
        if (s.BlacklistChangedAt is null) { s.IsBlacklisted = false; s.BlacklistReason = null; s.BlacklistedAt = null; }
        if (s.NotesChangedAt is null) s.Notes = null;
        return s;
    }

    /// <summary>Sunucudaki <c>WithoutScrubbedUnits()</c>'ın aynası: KVKK boşaltmasının
    /// sildiği birimler (ad, takma ad, adres bloğu, telefon, e-posta, TCKN, izinler)
    /// çıkarılır — silinmiş kaynaktaki "damgalı boş" bilinçli silme değildir, hedefin
    /// verisini silmemeli. Kalan: GroupId, alıcı ödemeli, kara liste, notlar.</summary>
    public CustomerSyncState WithoutScrubbedUnits()
    {
        var s = Clone();
        s.FullName = null; s.FullNameChangedAt = null;
        s.DisplayName = null; s.DisplayNameChangedAt = null;
        s.Address = null; s.City = null; s.District = null; s.AddressChangedAt = null;
        s.Phone = null; s.PhoneChangedAt = null;
        s.Email = null; s.EmailChangedAt = null;
        s.Tckn = null; s.TcknChangedAt = null;
        s.WhatsAppConsent = false; s.WhatsAppConsentChangedAt = null;
        s.SmsConsent = false; s.SmsConsentChangedAt = null;
        return s;
    }

    /// <summary>
    /// Shopper beyanı (kural 7, U3a, U9). Geçici satırı yalnız Shopper kaydı açar ve beyanı
    /// FullName/Phone/Address'tir (S17); eski <c>since</c> ingest'i beyan adını HER sürümde yalnız
    /// yerel <c>DisplayName</c>'e yazdı — yerel <c>FullName</c>'e HİÇBİR ZAMAN değil, o yalnız
    /// formdan (yayıncı verisi) gelir. Bu yüzden FullName burada hiç dokunulmaz (M-1 kalite
    /// incelemesi): silinmiş bir geçici kaydın beyanı, kaynağı apayrı olan yerel gerçek adı
    /// silemez (kural 8 — kimliğe yayılmaz). Takma ad, adres bloğu, telefon — bu üç birimden
    /// DAMGASIZ olup değeri beyana eşit olan çıkar — beyandır; farklı olan kalır (göç öncesi
    /// yayıncı düzeltmesi olabilir). Damgalı birim ve beyan olamayan birimler (ad, GroupId,
    /// alıcı ödemeli, e-posta, TCKN, izinler, kara liste, not) hiç değişmez.
    /// <paramref name="claims"/> null = beyan bilinmiyor (silinmiş geçici satır; devralma sonrası
    /// kopya — S19): beyan olabilen damgasız birimlerin hepsi çıkar. Altı alanı da (FullName,
    /// DisplayName, Phone, Address, City, District) boş, NON-null bir nesne de AYNI sayılır
    /// (M-2 — "boş beyan = bilinmiyor"): gerçek bir Shopper kaydı bunların hiçbirini boş
    /// bırakamaz (kayıt ad/telefon/adres ister), böyle bir nesne yalnız silinmiş/boşaltılmış bir
    /// geçici satırdan gelir — "bilinen ama hiçbiri eşleşmiyor" (hepsini KORU) sayılırsa KVKK
    /// niyetinin tersi olur.
    /// </summary>
    public CustomerSyncState WithoutShopperClaims(CustomerSyncState? claims)
    {
        if (claims is not null && IsBlankClaims(claims)) claims = null;

        var s = Clone();
        if (s.DisplayNameChangedAt is null
            && (claims is null
                || CustomerUnitMerge.SameText(s.DisplayName, claims.FullName)
                || CustomerUnitMerge.SameText(s.DisplayName, claims.DisplayName)))
            s.DisplayName = null;
        if (s.AddressChangedAt is null && (claims is null || SameAddressBlock(s, claims)))
        {
            s.Address = null; s.City = null; s.District = null;
        }
        if (s.PhoneChangedAt is null && (claims is null || SamePhone(s.Phone, claims.Phone)))
            s.Phone = null;
        return s;
    }

    /// <summary>Beyan nesnesinin altı alanı da (FullName, DisplayName, Phone, Address, City,
    /// District) boş mu — M-2. Gerçek bir Shopper kaydı bunların hiçbirini boş bırakamaz;
    /// böyle bir nesne yalnız silinmiş/boşaltılmış bir geçici satırdan gelir.</summary>
    private static bool IsBlankClaims(CustomerSyncState claims)
        => string.IsNullOrWhiteSpace(claims.FullName) && string.IsNullOrWhiteSpace(claims.DisplayName)
           && string.IsNullOrWhiteSpace(claims.Phone) && string.IsNullOrWhiteSpace(claims.Address)
           && string.IsNullOrWhiteSpace(claims.City) && string.IsNullOrWhiteSpace(claims.District);

    /// <summary>Blok olarak eşit: her parça ya iki tarafta da boş ya da aynı metin.</summary>
    private static bool SameAddressBlock(CustomerSyncState a, CustomerSyncState b)
        => SamePart(a.Address, b.Address) && SamePart(a.City, b.City) && SamePart(a.District, b.District);

    private static bool SamePart(string? a, string? b)
        => string.IsNullOrWhiteSpace(a) ? string.IsNullOrWhiteSpace(b) : CustomerUnitMerge.SameText(a, b);

    /// <summary>Aynı numara, farklı yazım ("+90 555…" / "0555…") — arama anahtarının normalizasyonu.</summary>
    private static bool SamePhone(string? a, string? b)
    {
        var key = CustomerSearch.NormalizePhoneKey(a);
        return key.Length > 0 && key == CustomerSearch.NormalizePhoneKey(b);
    }
}
