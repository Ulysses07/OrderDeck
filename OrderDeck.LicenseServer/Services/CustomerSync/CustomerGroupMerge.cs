using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.CustomerSync;

/// <summary>
/// Projeksiyona gelen grup verisinin nasıl yazılacağı. Üç kural:
///
/// <para><b>Apply</b> (aynı Id'den yeni istemci gönderimi): damgalı grup
/// mevcut damgadan yeniyse tamamen yazılır (son yazan kazanır). Damgasız grup
/// geçmiş veridir — güvenilir zamanı yok — yalnız boş alanları doldurur.</para>
///
/// <para><b>FillEmpty</b> (kopya → asıl kayıt): damgadan bağımsız, yalnız boş
/// alanları doldurur. Sohbetten yeni açılmış, takma addan başka bilgisi
/// olmayan bir kopya, gerçek adı "daha yeni" diye ezmesin.</para>
///
/// <para><b>ApplyLegacy</b> (damgasız eski sürüm): eskiden koşulsuz yazdığı
/// ad/telefon/adresi, yalnız o grup henüz hiç damgalanmamışsa yazar — eski
/// bir bilgisayar yeni sürümün girdiğini silemesin.</para>
///
/// Silinmiş (PurgedAt) hedefe hiçbir kural yazmaz. Metinler kolon sınırına
/// kırpılır: sınırı aşan tek bir satır partiyi 500'e düşürüp istemcinin
/// kuyruğunu kalıcı kilitlerdi (2026-08-14 olayı).
/// </summary>
public static class CustomerGroupMerge
{
    public const int NameMax = 200, GroupIdMax = 64, PhoneMax = 20, AddressMax = 500,
        CityMax = 64, EmailMax = 254, NotesMax = 2000, ReasonMax = 500;
    // TCKN için sınır YOK: ContactGroup.TcknProtected şifreli metindir ve
    // kırpılırsa bir daha çözülemez. Düz metin sınırı sync ucunda, şifrelemeden önce.

    public static bool Apply(WpfCustomerProjection t, CustomerGroups g)
    {
        if (t.PurgedAt is not null) return false;
        var changed = false;

        if (g.Identity.ChangedAt is { } ic) { if (Newer(ic, t.IdentityChangedAt)) changed |= WriteIdentity(t, g.Identity); }
        else changed |= FillIdentity(t, g.Identity);

        if (g.Address.ChangedAt is { } ac) { if (Newer(ac, t.AddressChangedAt)) changed |= WriteAddress(t, g.Address); }
        else changed |= FillAddress(t, g.Address);

        if (g.Contact.ChangedAt is { } cc) { if (Newer(cc, t.ContactChangedAt)) changed |= WriteContact(t, g.Contact); }
        else changed |= FillContact(t, g.Contact);

        if (g.Blacklist.ChangedAt is { } bc) { if (Newer(bc, t.BlacklistChangedAt)) changed |= WriteBlacklist(t, g.Blacklist); }
        else changed |= FillBlacklist(t, g.Blacklist);

        if (g.Notes.ChangedAt is { } nc) { if (Newer(nc, t.NotesChangedAt)) changed |= WriteNotes(t, g.Notes); }
        else changed |= FillNotes(t, g.Notes);

        return changed;
    }

    public static bool FillEmpty(WpfCustomerProjection t, CustomerGroups g)
    {
        if (t.PurgedAt is not null) return false;
        // Tek `|` BİLEREK: beş Fill* çağrısının HEPSİ çalışmalı (her biri
        // kendi grubunu doldurur) — `||` kullansaydık ilk true'dan sonra
        // kısa devre yapıp sonraki grupları hiç doldurmazdı.
        var changed = FillIdentity(t, g.Identity) | FillAddress(t, g.Address) | FillContact(t, g.Contact)
                    | FillBlacklist(t, g.Blacklist) | FillNotes(t, g.Notes);
        changed |= MaxStamps(t, g);
        return changed;
    }

    public static bool ApplyLegacy(WpfCustomerProjection t, string? fullName, string? phone, string? address)
    {
        if (t.PurgedAt is not null) return false;
        var changed = false;
        if (t.IdentityChangedAt is null) changed |= Set(() => t.FullName, v => t.FullName = v, Clip(fullName, NameMax));
        if (t.ContactChangedAt is null) changed |= Set(() => t.Phone, v => t.Phone = v, Clip(phone, PhoneMax));
        if (t.AddressChangedAt is null) changed |= Set(() => t.Address, v => t.Address = v, Clip(address, AddressMax));
        return changed;
    }

    // ── grup yazıcıları ─────────────────────────────────────────────

    // Write* yalnız damga yeniyken çağrılır; damga değiştiği için dönüş hep true.
    private static bool WriteIdentity(WpfCustomerProjection t, IdentityGroup g)
    {
        t.FullName = Clip(g.FullName, NameMax);
        t.DisplayName = Clip(g.DisplayName, NameMax);
        t.GroupId = Clip(g.GroupId, GroupIdMax);
        t.IdentityChangedAt = g.ChangedAt;
        return true;
    }

    private static bool WriteAddress(WpfCustomerProjection t, AddressGroup g)
    {
        Set(() => t.Address, v => t.Address = v, Clip(g.Address, AddressMax));
        Set(() => t.City, v => t.City = v, Clip(g.City, CityMax));
        Set(() => t.District, v => t.District = v, Clip(g.District, CityMax));
        t.RecipientPaysActive = g.RecipientPaysActive;
        t.AddressChangedAt = g.ChangedAt;
        return true;
    }

    private static bool WriteContact(WpfCustomerProjection t, ContactGroup g)
    {
        Set(() => t.Phone, v => t.Phone = v, Clip(g.Phone, PhoneMax));
        Set(() => t.Email, v => t.Email = v, Clip(g.Email, EmailMax));
        Set(() => t.TcknProtected, v => t.TcknProtected = v, g.TcknProtected);
        t.WhatsAppConsent = g.WhatsAppConsent;
        t.SmsConsent = g.SmsConsent;
        t.ContactChangedAt = g.ChangedAt;
        return true;
    }

    private static bool WriteBlacklist(WpfCustomerProjection t, BlacklistGroup g)
    {
        t.IsBlacklisted = g.IsBlacklisted;
        t.BlacklistReason = Clip(g.Reason, ReasonMax);
        t.BlacklistedAt = g.BlacklistedAt;
        t.BlacklistChangedAt = g.ChangedAt;
        return true;
    }

    private static bool WriteNotes(WpfCustomerProjection t, NotesGroup g)
    {
        t.Notes = Clip(g.Notes, NotesMax);
        t.NotesChangedAt = g.ChangedAt;
        return true;
    }

    // ── doldurucular (yalnız boş alan) ───────────────────────────────

    private static bool FillIdentity(WpfCustomerProjection t, IdentityGroup g)
        // Tek `|`: üç Fill çağrısının hepsi çalışmalı — FullName dolu diye
        // `||` ile kısa devre yapılırsa DisplayName/GroupId hiç denenmez.
        => Fill(() => t.FullName, v => t.FullName = v, Clip(g.FullName, NameMax))
         | Fill(() => t.DisplayName, v => t.DisplayName = v, Clip(g.DisplayName, NameMax))
         | Fill(() => t.GroupId, v => t.GroupId = v, Clip(g.GroupId, GroupIdMax));

    private static bool FillAddress(WpfCustomerProjection t, AddressGroup g)
    {
        // Tek `|`: Address/City/District'in hepsi denenmeli, biri dolu diye
        // diğerleri atlanmasın.
        var c = Fill(() => t.Address, v => t.Address = v, Clip(g.Address, AddressMax))
              | Fill(() => t.City, v => t.City = v, Clip(g.City, CityMax))
              | Fill(() => t.District, v => t.District = v, Clip(g.District, CityMax));
        if (t.AddressChangedAt is null && g.RecipientPaysActive && !t.RecipientPaysActive)
        {
            t.RecipientPaysActive = true;
            c = true;
        }
        return c;
    }

    private static bool FillContact(WpfCustomerProjection t, ContactGroup g)
    {
        // Tek `|`: Phone/Email/Tckn'in hepsi denenmeli.
        var c = Fill(() => t.Phone, v => t.Phone = v, Clip(g.Phone, PhoneMax))
              | Fill(() => t.Email, v => t.Email = v, Clip(g.Email, EmailMax))
              | Fill(() => t.TcknProtected, v => t.TcknProtected = v, g.TcknProtected);
        // İzinler yalnız grup hiç damgalanmamışken taşınır: damgalı grupta
        // izin, kişinin bilerek verdiği son karardır; geçmiş bir "evet" onu
        // geri açmamalı.
        if (t.ContactChangedAt is null)
        {
            if (g.WhatsAppConsent && !t.WhatsAppConsent) { t.WhatsAppConsent = true; c = true; }
            if (g.SmsConsent && !t.SmsConsent) { t.SmsConsent = true; c = true; }
        }
        return c;
    }

    private static bool FillBlacklist(WpfCustomerProjection t, BlacklistGroup g)
    {
        // Kara liste güvenlik bilgisi: hiçbir kopyada kaybolmamalı.
        if (!g.IsBlacklisted || t.IsBlacklisted) return false;
        t.IsBlacklisted = true;
        t.BlacklistReason ??= Clip(g.Reason, ReasonMax);
        t.BlacklistedAt ??= g.BlacklistedAt;
        return true;
    }

    private static bool FillNotes(WpfCustomerProjection t, NotesGroup g)
        => Fill(() => t.Notes, v => t.Notes = v, Clip(g.Notes, NotesMax));

    private static bool MaxStamps(WpfCustomerProjection t, CustomerGroups g)
    {
        var c = false;
        c |= MaxStamp(t.IdentityChangedAt, g.Identity.ChangedAt, v => t.IdentityChangedAt = v);
        c |= MaxStamp(t.AddressChangedAt, g.Address.ChangedAt, v => t.AddressChangedAt = v);
        c |= MaxStamp(t.ContactChangedAt, g.Contact.ChangedAt, v => t.ContactChangedAt = v);
        c |= MaxStamp(t.BlacklistChangedAt, g.Blacklist.ChangedAt, v => t.BlacklistChangedAt = v);
        c |= MaxStamp(t.NotesChangedAt, g.Notes.ChangedAt, v => t.NotesChangedAt = v);
        return c;
    }

    // ── yardımcılar ───────────────────────────────────────────────────

    private static bool Newer(DateTimeOffset incoming, DateTimeOffset? current)
        => current is null || incoming > current.Value;

    private static bool MaxStamp(DateTimeOffset? current, DateTimeOffset? incoming, Action<DateTimeOffset?> set)
    {
        if (incoming is null || (current is not null && current.Value >= incoming.Value)) return false;
        set(incoming);
        return true;
    }

    private static bool Set(Func<string?> get, Action<string?> set, string? value)
    {
        if (get() == value) return false;
        set(value);
        return true;
    }

    private static bool Fill(Func<string?> get, Action<string?> set, string? value)
    {
        if (!string.IsNullOrWhiteSpace(get()) || string.IsNullOrWhiteSpace(value)) return false;
        set(value);
        return true;
    }

    internal static string? Clip(string? s, int max)
    {
        if (s is null || s.Length <= max) return s;
        // Vekil (surrogate) çiftinin ortasından KESME: kesim noktasındaki
        // son karakter bir yüksek vekilse (çiftin ilk yarısı), eşleniği
        // (alçak vekil) tam kesim sınırında kalır ve dışarıda kalır — sonuç
        // tek başına geçersiz bir vekil ile biter. TikTok/Instagram takma
        // adlarında ve notlarda emoji yaygın; bir karakter daha geriden
        // keserek çifti bütün bırakıyoruz.
        var cut = char.IsHighSurrogate(s[max - 1]) ? max - 1 : max;
        return s[..cut];
    }
}
