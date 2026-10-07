namespace OrderDeck.Core.Customers;

/// <summary>
/// Birim birleştirme — SUNUCUDAKİ <c>CustomerFieldMerge.Apply</c>'ın yerel aynası
/// (OrderDeck.LicenseServer/Services/CustomerSync/CustomerFieldMerge.cs). İki taraf aynı
/// kuralı uygulamazsa satır bilgisayarlar arasında gidip gelir ya da kalıcı ayrışır:
/// bu dosya değişirse sunucudaki de değişmeli (ve tersi).
///
/// <para><b>Kural:</b> damgalı gelen birim hedeften YENİYSE aynen yazılır (boş =
/// bilinçli silme) ve damgayı taşır; eski damga hiçbir şey yapmaz; eşit damga yankıdır
/// (yazılmaz) — TEK istisna <c>incomingWinsTie</c>: sunucudan inen satır uygulanırken eşit
/// damgada değer FARKLIYSA sunucunun değeri yazılır. Damgasız gelen birim yalnız hedef birim
/// de damgasızsa boşları doldurur. Silinmiş (PurgedAt) hedefe hiçbir şey yazılmaz.</para>
///
/// <para><b>Sunucudan BİLEREK farklı üç nokta:</b> (1) metin kırpılmaz — yerel kolonların
/// sınırı yok, sunucu kendi yazımında kırpar; (2) damgasız FullName bir TAKMA ADSA
/// (yerelin ya da gelenin DisplayName'i ya da kullanıcı adıyla aynı) hiç kullanılmaz,
/// boş yerel ada da doldurulmaz — sunucu eski sürümün takma ad yedeğini (R3-02)
/// FullName'de tutuyor olabilir; (3) <c>incomingWinsTie</c> (C2 kalite incelemesi):
/// sunucu eşit damgada İLK GELENİ tutar, istemci sunucudan inen satırda eşit damgalı farklı
/// değeri alır — bütün bilgisayarlar sunucunun değerine yakınsar. Yoksa aynı formu haberleşmeden
/// işleyen iki bilgisayarın farklı türettiği grup eşit damgayla kalıcı ayrışırdı.</para>
/// </summary>
public static class CustomerUnitMerge
{
    // Sunucunun kolon sınırları (CustomerFieldMerge.NameMax …) — YALNIZ eşit damga
    // karşılaştırmasında: sunucu yazarken kırpar, yerel kırpmaz; kendi değerinin kırpılmış
    // yankısı "farklı" sayılmamalı (yoksa yerel uzun not kırpılırdı).
    private const int NameMax = 200, GroupIdMax = 64, PhoneMax = 20, AddressMax = 500,
        CityMax = 64, EmailMax = 254, NotesMax = 2000, ReasonMax = 500;

    /// <param name="incomingWinsTie">Sunucudan inen satır yerel satıra uygulanırken TRUE (C4
    /// <c>ApplyServerCustomer</c>): eşit damgada değer farklıysa gelen (sunucunun) değer yazılır,
    /// damga aynı kalır. Yerel kopya→asıl birleştirmesinde ve sunucunun birleştirmesini taklit eden
    /// her yerde FALSE (sunucuyla aynı: eşit damga yankıdır).</param>
    public static bool Apply(CustomerSyncState t, CustomerSyncState f, bool incomingWinsTie = false)
    {
        if (t.PurgedAt is not null) return false;
        var tie = incomingWinsTie;

        // Tek `|` BİLEREK (sunucuyla aynı): her birim değerlendirilmeli; `||` ilk
        // değişiklikte kısa devre yapardı. Takma ad addan önce: ad kuralı güncel
        // takma adı kullanır.
        return
            Unit(f.DisplayNameChangedAt, t.DisplayNameChangedAt, at => t.DisplayNameChangedAt = at,
                write: () => t.DisplayName = Norm(f.DisplayName),
                fill: () => Fill(t.DisplayName, f.DisplayName, v => t.DisplayName = v),
                tieDiffers: tie && !SameAsServer(t.DisplayName, f.DisplayName, NameMax))
          | Unit(f.FullNameChangedAt, t.FullNameChangedAt, at => t.FullNameChangedAt = at,
                write: () => t.FullName = Norm(f.FullName),
                fill: () => FillFullName(t, f),
                tieDiffers: tie && !SameAsServer(t.FullName, f.FullName, NameMax))
          | Unit(f.GroupIdChangedAt, t.GroupIdChangedAt, at => t.GroupIdChangedAt = at,
                write: () => t.GroupId = Norm(f.GroupId),
                fill: () => Fill(t.GroupId, f.GroupId, v => t.GroupId = v),
                tieDiffers: tie && !SameAsServer(t.GroupId, f.GroupId, GroupIdMax))
          | Unit(f.AddressChangedAt, t.AddressChangedAt, at => t.AddressChangedAt = at,
                write: () =>
                {
                    t.Address = Norm(f.Address);
                    t.City = Norm(f.City);
                    t.District = Norm(f.District);
                },
                fill: () => FillAddressBlock(t, f),
                tieDiffers: tie && !(SameAsServer(t.Address, f.Address, AddressMax)
                                     && SameAsServer(t.City, f.City, CityMax)
                                     && SameAsServer(t.District, f.District, CityMax)))
          | Unit(f.RecipientPaysChangedAt, t.RecipientPaysChangedAt, at => t.RecipientPaysChangedAt = at,
                write: () => t.RecipientPaysActive = f.RecipientPaysActive,
                fill: () => FillFlag(t.RecipientPaysActive, f.RecipientPaysActive, () => t.RecipientPaysActive = true),
                tieDiffers: tie && t.RecipientPaysActive != f.RecipientPaysActive)
          | Unit(f.PhoneChangedAt, t.PhoneChangedAt, at => t.PhoneChangedAt = at,
                write: () => t.Phone = Norm(f.Phone),
                fill: () => Fill(t.Phone, f.Phone, v => t.Phone = v),
                tieDiffers: tie && !SameAsServer(t.Phone, f.Phone, PhoneMax))
          | Unit(f.EmailChangedAt, t.EmailChangedAt, at => t.EmailChangedAt = at,
                write: () => t.Email = Norm(f.Email),
                fill: () => Fill(t.Email, f.Email, v => t.Email = v),
                tieDiffers: tie && !SameAsServer(t.Email, f.Email, EmailMax))
          | Unit(f.TcknChangedAt, t.TcknChangedAt, at => t.TcknChangedAt = at,
                write: () => t.Tckn = Norm(f.Tckn),
                fill: () => Fill(t.Tckn, f.Tckn, v => t.Tckn = v),
                // S13: sunucu TCKN'yi çözemezse null/boş gönderir, damga değişmez — eşit damgalı
                // boş "silme" değildir; yerel düz metin (anahtar kaybında tek kurtarılabilir kopya)
                // korunur (M-3: boş string de null kadar "çözülemedi" sayılır — IsBlank, yalnız null değil).
                tieDiffers: tie && !IsBlank(f.Tckn) && !SameAsServer(t.Tckn, f.Tckn, int.MaxValue))
          | Unit(f.WhatsAppConsentChangedAt, t.WhatsAppConsentChangedAt, at => t.WhatsAppConsentChangedAt = at,
                write: () => t.WhatsAppConsent = f.WhatsAppConsent,
                fill: () => FillFlag(t.WhatsAppConsent, f.WhatsAppConsent, () => t.WhatsAppConsent = true),
                tieDiffers: tie && t.WhatsAppConsent != f.WhatsAppConsent)
          | Unit(f.SmsConsentChangedAt, t.SmsConsentChangedAt, at => t.SmsConsentChangedAt = at,
                write: () => t.SmsConsent = f.SmsConsent,
                fill: () => FillFlag(t.SmsConsent, f.SmsConsent, () => t.SmsConsent = true),
                tieDiffers: tie && t.SmsConsent != f.SmsConsent)
          | Unit(f.BlacklistChangedAt, t.BlacklistChangedAt, at => t.BlacklistChangedAt = at,
                write: () =>
                {
                    t.IsBlacklisted = f.IsBlacklisted;
                    t.BlacklistReason = Norm(f.BlacklistReason);
                    t.BlacklistedAt = f.BlacklistedAt;
                },
                fill: () => FillBlacklist(t, f),
                tieDiffers: tie && !(t.IsBlacklisted == f.IsBlacklisted
                                     && SameAsServer(t.BlacklistReason, f.BlacklistReason, ReasonMax)
                                     && t.BlacklistedAt == f.BlacklistedAt))
          | Unit(f.NotesChangedAt, t.NotesChangedAt, at => t.NotesChangedAt = at,
                write: () => t.Notes = Norm(f.Notes),
                fill: () => Fill(t.Notes, f.Notes, v => t.Notes = v),
                tieDiffers: tie && !SameAsServer(t.Notes, f.Notes, NotesMax));
    }

    private static bool Unit(long? incomingAt, long? targetAt, Action<long> setStamp, Action write, Func<bool> fill,
        bool tieDiffers)
    {
        if (incomingAt is { } at)
        {
            if (targetAt is { } current)
            {
                if (at < current) return false;
                if (at == current)
                {
                    // Eşit damga yankıdır; değer farklıysa yalnız incomingWinsTie'de gelen yazılır
                    // (damga zaten eşit — yeniden yazılmaz).
                    if (!tieDiffers) return false;
                    write();
                    return true;
                }
            }
            write();
            setStamp(at);
            return true;
        }
        return targetAt is null && fill();
    }

    /// <summary>Eşit damgada yakınsamanın ölçüsü: yerel değer sunucunun yazacağı biçime (boş → null,
    /// sınırda kırpılmış) getirilip gelen değerle BİREBİR (ordinal) karşılaştırılır — harf farkı da
    /// farktır; hedef, sunucudaki değerin aynısı. Kırpma SONRASI dış <c>Norm</c> şart (M-4): kırpılan
    /// önek boşluktan ibaret kalırsa (ör. uzun değerin ilk <c>max</c> karakteri boşluk, gerisi dolu)
    /// sonuç null'a döner — sunucu da aynı değeri kendi <c>Norm(..., max)</c>'ıyla yazmış olsaydı
    /// boşu bütün değer üstünden (kırpmadan ÖNCE) kontrol ederdi; iki taraf aynı "boş" sonucuna
    /// gelmeli, yoksa kırpılmış önek yanlışlıkla "farklı" sayılıp yerel değer boşa yazılırdı.</summary>
    private static bool SameAsServer(string? local, string? incoming, int max)
        => string.Equals(Norm(Clip(Norm(local), max)), Norm(incoming), StringComparison.Ordinal);

    /// <summary>Sunucudaki <c>CustomerFieldMerge.Clip</c>'in aynası (vekil çifti bölünmez).</summary>
    private static string? Clip(string? s, int max)
    {
        if (s is null || s.Length <= max) return s;
        var cut = char.IsHighSurrogate(s[max - 1]) ? max - 1 : max;
        return s[..cut];
    }

    // ── doldurucular (yalnız damgasız hedefte çağrılır) ─────────────────

    /// <summary>Sunucudaki FillOrUpgradeName + istemci kuralı: gelen ad bir takma adsa
    /// (yerelin ya da gelenin DisplayName'i ya da kullanıcı adı; harf ve İ/ı farkı yok
    /// sayılır) HİÇ kullanılmaz. Yerel ad boşsa gerçek ad doldurulur; yerel ad bir takma
    /// adsa ve farklı bir gerçek ad geliyorsa onun yerine geçer.</summary>
    private static bool FillFullName(CustomerSyncState t, CustomerSyncState f)
    {
        var incoming = Norm(f.FullName);
        if (incoming is null) return false;
        bool IsNickname(string? s) =>
            SameText(s, t.DisplayName) || SameText(s, f.DisplayName) || SameText(s, t.Username);
        if (IsNickname(incoming)) return false;
        if (IsBlank(t.FullName))
        {
            t.FullName = incoming;
            return true;
        }
        if (SameText(incoming, t.FullName) || !IsNickname(t.FullName)) return false;
        t.FullName = incoming;
        return true;
    }

    /// <summary>Adres bloğu bütün doldurulur: hedef boşsa gelen blok tamamen alınır;
    /// değilse eksik parçalar yalnız iki tarafta dolu olan parçalar AYNIYSA ve en az bir
    /// ortak dolu parça varsa tamamlanır (sunucu FillAddressBlock).</summary>
    private static bool FillAddressBlock(CustomerSyncState t, CustomerSyncState f)
    {
        var address = Norm(f.Address);
        var city = Norm(f.City);
        var district = Norm(f.District);
        var targetEmpty = IsBlank(t.Address) && IsBlank(t.City) && IsBlank(t.District);
        if (!targetEmpty)
        {
            if (!Compatible(t.Address, address) || !Compatible(t.City, city) || !Compatible(t.District, district))
                return false;
            if (!SameText(t.Address, address) && !SameText(t.City, city) && !SameText(t.District, district))
                return false;
        }
        return Fill(t.Address, address, v => t.Address = v)
             | Fill(t.City, city, v => t.City = v)
             | Fill(t.District, district, v => t.District = v);
    }

    /// <summary>Kara liste güvenlik bilgisidir: damgasız iki kayıt birleşirken kaybolmaz;
    /// hedefteki sebep ve tarih korunur.</summary>
    private static bool FillBlacklist(CustomerSyncState t, CustomerSyncState f)
    {
        if (!f.IsBlacklisted || t.IsBlacklisted) return false;
        t.IsBlacklisted = true;
        if (IsBlank(t.BlacklistReason)) t.BlacklistReason = Norm(f.BlacklistReason);
        t.BlacklistedAt ??= f.BlacklistedAt;
        return true;
    }

    /// <summary>Damgasız bayrak "evet"i taşır, "hayır"ı taşımaz.</summary>
    private static bool FillFlag(bool current, bool incoming, Action setTrue)
    {
        if (current || !incoming) return false;
        setTrue();
        return true;
    }

    private static bool Fill(string? current, string? incoming, Action<string?> set)
    {
        if (!IsBlank(current)) return false;
        var value = Norm(incoming);
        if (value is null) return false;
        set(value);
        return true;
    }

    // ── metin yardımcıları (sunucuyla birebir) ─────────────────────────

    private static bool IsBlank(string? s) => string.IsNullOrWhiteSpace(s);

    private static bool Compatible(string? current, string? incoming)
        => IsBlank(current) || incoming is null || SameText(current, incoming);

    /// <summary>Kırpılmış, küçük harf; Türkçe I/İ/ı/i ve harf büyüklüğü farkı yok
    /// sayılır, aksan sayılmaz ("Karşıyaka" ≠ "Karsiyaka"). Kimlik anahtarıyla
    /// (<see cref="CustomerIdentity.KeyOf"/>) KARIŞTIRMA — o ı→i yapmaz.</summary>
    public static bool SameText(string? a, string? b)
        => !IsBlank(a) && !IsBlank(b) && Key(a!) == Key(b!);

    private static string Key(string s)
        => s.Trim().ToLowerInvariant().Replace('İ', 'i').Replace('ı', 'i');

    private static string? Norm(string? s) => IsBlank(s) ? null : s;
}
