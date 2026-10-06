using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.CustomerSync;

/// <summary>
/// Projeksiyona gelen alanların yazılma kuralı. Her birim (bkz.
/// <see cref="CustomerSyncFields"/>) kendi damgasıyla, ötekilerden bağımsız
/// değerlendirilir:
///
/// <para><b>Damgalı gelen birim</b> hedefin damgasından YENİYSE birimi aynen
/// yazar — boş değer bilinçli silmedir — ve damgayı taşır. Eşit ya da eski
/// damga hiçbir şey yapmaz; bir bilgisayarın kendi gönderiminin yankısı
/// böylece yazım üretmez.</para>
///
/// <para><b>Damgasız gelen birim</b> geçmiş veridir (güvenilir zamanı yok):
/// yalnız hedef birim de hiç damgalanmamışsa boşları doldurur. Damgalı hedef
/// bilinçli son karardır, bilinçli silme dahil. Ayrıca veri değişirse damga
/// da değişmeli: damga aynı kalıp veri değişseydi, o damgayı zaten bilen
/// bilgisayarlar farkı hiç almaz ve kalıcı olarak ayrışırdı (2026-10-06 A3
/// kalite incelemesi). Tek istisna, damgasız birimlerin birbirini doldurması
/// — orada karşılaştırılacak damga yok.</para>
///
/// <para>Aynı kural istemci gönderiminde ve kopya → asıl kayıt
/// birleştirmesinde geçerlidir. Sohbetten açılmış, yalnız takma adı olan bir
/// kopya gerçek adı ezemez: boş FullName hiç damgalanmaz (istemci yalnız dolu
/// ya da bilerek değiştirilen alanı damgalar).</para>
///
/// <para>Silinmiş (PurgedAt) hedefe hiçbir kural yazmaz. Metinler kolon
/// sınırına kırpılır — sınırı aşan tek satır partiyi 500'e düşürüp istemcinin
/// kuyruğunu kalıcı kilitlerdi (2026-08-14 olayı) — ve yalnız boşluktan
/// oluşan metin boş sayılır.</para>
/// </summary>
public static class CustomerFieldMerge
{
    public const int NameMax = 200, GroupIdMax = 64, PhoneMax = 20, AddressMax = 500,
        CityMax = 64, EmailMax = 254, NotesMax = 2000, ReasonMax = 500;
    // TCKN için sınır YOK: TcknProtected şifreli metindir, kırpılırsa bir daha
    // çözülemez. Düz metin sınırı sync ucunda, şifrelemeden önce.

    public static bool Apply(WpfCustomerProjection t, CustomerSyncFields f)
    {
        if (t.PurgedAt is not null) return false;

        // Tek `|` BİLEREK: her birim değerlendirilmeli; `||` ilk değişiklikten
        // sonra kısa devre yapıp kalan birimleri atlardı. Takma ad addan önce:
        // ad kuralı (FillFullName) güncel takma adı da kullanır.
        return
            Unit(f.DisplayNameChangedAt, t.DisplayNameChangedAt, at => t.DisplayNameChangedAt = at,
                write: () => t.DisplayName = Norm(f.DisplayName, NameMax),
                fill: () => Fill(t.DisplayName, f.DisplayName, NameMax, v => t.DisplayName = v))
          | Unit(f.FullNameChangedAt, t.FullNameChangedAt, at => t.FullNameChangedAt = at,
                write: () => t.FullName = Norm(f.FullName, NameMax),
                fill: () => FillFullName(t, f))
          | Unit(f.GroupIdChangedAt, t.GroupIdChangedAt, at => t.GroupIdChangedAt = at,
                write: () => t.GroupId = Norm(f.GroupId, GroupIdMax),
                fill: () => Fill(t.GroupId, f.GroupId, GroupIdMax, v => t.GroupId = v))
          | Unit(f.AddressChangedAt, t.AddressChangedAt, at => t.AddressChangedAt = at,
                write: () =>
                {
                    t.Address = Norm(f.Address, AddressMax);
                    t.City = Norm(f.City, CityMax);
                    t.District = Norm(f.District, CityMax);
                },
                fill: () => FillAddressBlock(t, f))
          | Unit(f.RecipientPaysChangedAt, t.RecipientPaysChangedAt, at => t.RecipientPaysChangedAt = at,
                write: () => t.RecipientPaysActive = f.RecipientPaysActive,
                fill: () => FillFlag(t.RecipientPaysActive, f.RecipientPaysActive, () => t.RecipientPaysActive = true))
          | Unit(f.PhoneChangedAt, t.PhoneChangedAt, at => t.PhoneChangedAt = at,
                write: () => t.Phone = Norm(f.Phone, PhoneMax),
                fill: () => Fill(t.Phone, f.Phone, PhoneMax, v => t.Phone = v))
          | Unit(f.EmailChangedAt, t.EmailChangedAt, at => t.EmailChangedAt = at,
                write: () => t.Email = Norm(f.Email, EmailMax),
                fill: () => Fill(t.Email, f.Email, EmailMax, v => t.Email = v))
          | Unit(f.TcknChangedAt, t.TcknChangedAt, at => t.TcknChangedAt = at,
                write: () => t.TcknProtected = Norm(f.TcknProtected),
                fill: () => Fill(t.TcknProtected, f.TcknProtected, int.MaxValue, v => t.TcknProtected = v))
          | Unit(f.WhatsAppConsentChangedAt, t.WhatsAppConsentChangedAt, at => t.WhatsAppConsentChangedAt = at,
                write: () => t.WhatsAppConsent = f.WhatsAppConsent,
                fill: () => FillFlag(t.WhatsAppConsent, f.WhatsAppConsent, () => t.WhatsAppConsent = true))
          | Unit(f.SmsConsentChangedAt, t.SmsConsentChangedAt, at => t.SmsConsentChangedAt = at,
                write: () => t.SmsConsent = f.SmsConsent,
                fill: () => FillFlag(t.SmsConsent, f.SmsConsent, () => t.SmsConsent = true))
          | Unit(f.BlacklistChangedAt, t.BlacklistChangedAt, at => t.BlacklistChangedAt = at,
                write: () =>
                {
                    t.IsBlacklisted = f.IsBlacklisted;
                    t.BlacklistReason = Norm(f.BlacklistReason, ReasonMax);
                    t.BlacklistedAt = f.BlacklistedAt;
                },
                fill: () => FillBlacklist(t, f))
          | Unit(f.NotesChangedAt, t.NotesChangedAt, at => t.NotesChangedAt = at,
                write: () => t.Notes = Norm(f.Notes, NotesMax),
                fill: () => Fill(t.Notes, f.Notes, NotesMax, v => t.Notes = v));
    }

    /// <summary>
    /// Eski sürüm (format 1) gönderimi: yalnız ad, telefon, adres; damgasız.
    /// O birim yeni sürümce damgalanmışsa hiçbiri yazmaz — eski bir bilgisayar
    /// yeni sürümün girdiğini silemesin.
    ///
    /// <para><b>Ad yalnız boşsa doldurulur:</b> eski sürüm gerçek ad yoksa
    /// takma adı gönderir (R3-02, WpfCustomerProjectionSyncService) ve
    /// birleştirmeden sonra asıl kayıt başka bilgisayarın gerçek adını
    /// taşıyabilir.</para>
    ///
    /// <para><b>Telefon/adres eskisi gibi son gönderimle güncellenir ama boş
    /// değer silmez:</b> birleştirmeden sonra asıl kayıt birkaç bilgisayarın
    /// verisini taşır; birinin boşu ötekinin değerini silmesin. Adres satırı
    /// il/ilçesi dolu bir bloğu değiştirmez, yalnız boşsa doldurur — eski
    /// sürüm il/ilçe bilmiyor, değiştirseydi etikete karışık adres basılırdı.</para>
    ///
    /// <para>Bedeli: eski sürümde yapılan ad düzeltmesi yeni sürüme geçilene
    /// kadar sunucuya yansımaz.</para>
    /// </summary>
    public static bool ApplyLegacy(WpfCustomerProjection t, string? fullName, string? phone, string? address)
    {
        if (t.PurgedAt is not null) return false;
        var changed = false;
        if (t.FullNameChangedAt is null)
            changed |= Fill(t.FullName, fullName, NameMax, v => t.FullName = v);
        if (t.PhoneChangedAt is null)
            changed |= Overwrite(t.Phone, phone, PhoneMax, v => t.Phone = v);
        if (t.AddressChangedAt is null)
            changed |= IsBlank(t.City) && IsBlank(t.District)
                ? Overwrite(t.Address, address, AddressMax, v => t.Address = v)
                : Fill(t.Address, address, AddressMax, v => t.Address = v);
        return changed;
    }

    // ── birim kuralı ───────────────────────────────────────────────────

    private static bool Unit(DateTimeOffset? incomingAt, DateTimeOffset? targetAt,
        Action<DateTimeOffset> setStamp, Action write, Func<bool> fill)
    {
        if (incomingAt is { } at)
        {
            if (targetAt is { } current && at <= current) return false;
            write();
            setStamp(at);
            return true;
        }
        return targetAt is null && fill();
    }

    // ── doldurucular (yalnız damgasız hedefte çağrılır) ─────────────────

    /// <summary>Eski sürüm gerçek ad yoksa takma adı FullName diye
    /// gönderiyordu (R3-02). Hedefteki damgasız ad bilinen bir takma adla
    /// AYNIYSA aslında takma addır: gelen gerçek ad onun yerine geçer. Yoksa
    /// birleştirilmiş kaydın takma adı, gerçek adı bilen bilgisayarın
    /// (damgasız, geçmiş) değerini kalıcı olarak engellerdi.</summary>
    private static bool FillFullName(WpfCustomerProjection t, CustomerSyncFields f)
    {
        if (Fill(t.FullName, f.FullName, NameMax, v => t.FullName = v)) return true;
        var incoming = Norm(f.FullName, NameMax);
        if (incoming is null || SameText(incoming, t.FullName)) return false;
        bool IsNickname(string? s) => SameText(s, t.DisplayName) || SameText(s, f.DisplayName);
        if (!IsNickname(t.FullName) || IsNickname(incoming)) return false;
        t.FullName = incoming;
        return true;
    }

    /// <summary>Adres bloğu bütün olarak doldurulur: hedef boşsa gelen blok
    /// tamamen alınır; değilse eksik parçalar yalnız iki tarafta da dolu olan
    /// parçalar AYNIYSA tamamlanır. Başka bir adresin il/ilçesiyle tamamlanan
    /// blok etikete karışık adres basar; emin olunamayınca doldurmamak
    /// karıştırmaktan iyidir.</summary>
    private static bool FillAddressBlock(WpfCustomerProjection t, CustomerSyncFields f)
    {
        var address = Norm(f.Address, AddressMax);
        var city = Norm(f.City, CityMax);
        var district = Norm(f.District, CityMax);
        if (!Compatible(t.Address, address) || !Compatible(t.City, city) || !Compatible(t.District, district))
            return false;
        return Fill(t.Address, address, AddressMax, v => t.Address = v)
             | Fill(t.City, city, CityMax, v => t.City = v)
             | Fill(t.District, district, CityMax, v => t.District = v);
    }

    /// <summary>Kara liste güvenlik bilgisidir: damgasız iki kayıt birleşirken
    /// kaybolmaz; hedefteki sebep ve tarih korunur.</summary>
    private static bool FillBlacklist(WpfCustomerProjection t, CustomerSyncFields f)
    {
        if (!f.IsBlacklisted || t.IsBlacklisted) return false;
        t.IsBlacklisted = true;
        if (IsBlank(t.BlacklistReason)) t.BlacklistReason = Norm(f.BlacklistReason, ReasonMax);
        t.BlacklistedAt ??= f.BlacklistedAt;
        return true;
    }

    /// <summary>Damgasız bayrak "evet"i taşır, "hayır"ı taşımaz: geçmiş veride
    /// false çoğunlukla "hiç ayarlanmadı" demektir.</summary>
    private static bool FillFlag(bool current, bool incoming, Action setTrue)
    {
        if (current || !incoming) return false;
        setTrue();
        return true;
    }

    private static bool Fill(string? current, string? incoming, int max, Action<string?> set)
    {
        if (!IsBlank(current)) return false;
        var value = Norm(incoming, max);
        if (value is null) return false;
        set(value);
        return true;
    }

    private static bool Overwrite(string? current, string? incoming, int max, Action<string?> set)
    {
        var value = Norm(incoming, max);
        if (value is null || value == current) return false;
        set(value);
        return true;
    }

    // ── metin yardımcıları ─────────────────────────────────────────────

    private static bool IsBlank(string? s) => string.IsNullOrWhiteSpace(s);

    private static bool Compatible(string? current, string? incoming)
        => IsBlank(current) || incoming is null || SameText(current, incoming);

    /// <summary>Kırpılmış, küçük harf karşılaştırma; Türkçe I/İ/ı/i ve
    /// büyük/küçük harf farkı yok sayılır ("İZMİR" = "izmir"). Aksan yok
    /// sayılmaz ("Karşıyaka" ≠ "Karsiyaka"): emin olunamayan durum "farklı"
    /// sayılır.</summary>
    internal static bool SameText(string? a, string? b)
        => !IsBlank(a) && !IsBlank(b) && Key(a!) == Key(b!);

    private static string Key(string s)
        => s.Trim().ToLowerInvariant().Replace('İ', 'i').Replace('ı', 'i');

    /// <summary>Boş ya da yalnız boşluktan oluşan metin null; değilse kolon
    /// sınırına kırpılır.</summary>
    internal static string? Norm(string? s, int max = int.MaxValue)
        => IsBlank(s) ? null : Clip(s, max);

    internal static string? Clip(string? s, int max)
    {
        if (s is null || s.Length <= max) return s;
        // Vekil (surrogate) çiftinin ortasından KESME: kesim noktasındaki son
        // karakter yüksek vekilse eşleniği sınırın dışında kalır ve sonuç tek
        // başına geçersiz bir vekille biter. TikTok/Instagram takma adlarında ve
        // notlarda emoji yaygın; bir karakter geriden keserek çifti bütün bırak.
        var cut = char.IsHighSurrogate(s[max - 1]) ? max - 1 : max;
        return s[..cut];
    }
}
